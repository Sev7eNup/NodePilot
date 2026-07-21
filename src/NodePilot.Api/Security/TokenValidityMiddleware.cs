using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NodePilot.Core.Enums;
using NodePilot.Data;

namespace NodePilot.Api.Security;

/// <summary>
/// Runs after JWT authentication to enforce two checks that JwtBearer cannot express on its own:
///
///   1. the token's <c>jti</c> claim is not in the <c>RevokedTokens</c> table, and
///   2. the user identified by the token is still <c>IsActive = true</c>.
///
/// A failure converts the authenticated principal into a 401 with a terse body — we never leak
/// which of the two checks failed so attackers can't use the response to distinguish
/// "this token is revoked" from "this user is disabled".
///
/// Performance: user-state lookups are cached in <see cref="IMemoryCache"/> for a short TTL (30 s).
/// Without the cache every authenticated request ran two DB round-trips — under heavy dashboard
/// polling that dominated the hot path. Revocation lookups only cache positive hits so logout
/// and refresh rotation take effect immediately after the revocation row is written.
/// </summary>
public class TokenValidityMiddleware
{
    internal const string InvalidatedPrincipalItem = "NodePilot.Security.InvalidatedPrincipal";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly RequestDelegate _next;

    public TokenValidityMiddleware(RequestDelegate next) { _next = next; }

    public async Task Invoke(
        HttpContext ctx,
        NodePilotDbContext db,
        IMemoryCache cache,
        IOptions<AuthenticationPolicyOptions>? authenticationPolicy = null,
        ExternalAuthorizationEvaluator? externalAuthorization = null)
    {
        var endpoint = ctx.GetEndpoint();
        var allowAnonymous = endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is not null;

        if (ctx.User?.Identity?.IsAuthenticated == true)
        {
            var jti = ctx.User.FindFirstValue(JwtRegisteredClaimNames.Jti);
            var userIdStr = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var sessionIdValue = ctx.User.FindFirstValue(AuthSessionIssuer.SessionIdClaim);
            var stampValue = ctx.User.FindFirstValue("np_secstamp");
            var roleClaims = ctx.User.FindAll(ClaimTypes.Role).ToArray();
            var isWindowsHandshake = endpoint?.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .SelectMany(data => (data.AuthenticationSchemes ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Any(scheme => string.Equals(
                    scheme, Hosting.AuthenticationSetup.WindowsAuthSchemeName,
                    StringComparison.Ordinal)) == true;
            if (isWindowsHandshake)
            {
                await _next(ctx);
                return;
            }
            if (string.IsNullOrEmpty(jti)
                || !Guid.TryParse(userIdStr, out var userId)
                || !Guid.TryParse(sessionIdValue, out var sessionId)
                || !int.TryParse(stampValue, out var requiredStamp)
                || roleClaims.Length != 1
                || !Enum.TryParse<UserRole>(roleClaims[0].Value, ignoreCase: false, out var requiredRole))
            {
                await RejectOrAnonymizeAsync(ctx, allowAnonymous);
                return;
            }

            if (!string.IsNullOrEmpty(jti))
            {
                var revokedKey = "tv:jti-revoked:" + jti;
                var revoked = cache.TryGetValue<bool>(revokedKey, out var cachedRevoked)
                    && cachedRevoked;
                if (!revoked)
                {
                    revoked = await db.RevokedTokens.AsNoTracking().AnyAsync(r => r.Jti == jti);
                    if (revoked)
                        cache.Set(revokedKey, true, CacheTtl);
                }
                if (revoked)
                {
                    await RejectOrAnonymizeAsync(ctx, allowAnonymous);
                    return;
                }
            }
            if (Guid.TryParse(userIdStr, out userId))
            {
                if (Guid.TryParse(sessionIdValue, out sessionId))
                {
                    var now = DateTime.UtcNow;
                    var activeSession = await db.AuthSessions.AsNoTracking()
                        .Where(s => s.Id == sessionId && s.UserId == userId)
                        .Select(s => new
                        {
                            s.RevokedAt,
                            s.ExpiresAt,
                            s.AuthorizationVersion,
                            s.CurrentJti,
                        })
                        .FirstOrDefaultAsync();
                    if (activeSession is null
                        || activeSession.RevokedAt is not null
                        || activeSession.ExpiresAt <= now
                        || !string.Equals(activeSession.CurrentJti, jti, StringComparison.Ordinal))
                    {
                        await RejectOrAnonymizeAsync(ctx, allowAnonymous);
                        return;
                    }

                    if (activeSession.AuthorizationVersion != requiredStamp)
                    {
                        await RejectOrAnonymizeAsync(ctx, allowAnonymous);
                        return;
                    }
                }

                var maxStaleness = Math.Clamp(
                    authenticationPolicy?.Value.MaxAuthorizationStalenessMinutes ?? 15,
                    1,
                    15);
                var userKey = UserSessionInvalidation.UserStateCacheKey(userId);
                var userState = await cache.GetOrCreateAsync(userKey, async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CacheTtl;
                    var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId);
                    if (u is null) return null;
                    var authorizationCurrent = true;
                    DateTime? authorizationValidUntil = null;
                    if (u.Provider != AuthProvider.Local)
                    {
                        if (externalAuthorization is not null)
                        {
                            var evaluation = await externalAuthorization.EvaluateAsync(
                                u, DateTime.UtcNow, ctx.RequestAborted);
                            authorizationCurrent = evaluation.IsCurrent;
                            authorizationValidUntil = evaluation.ValidUntil;
                        }
                        else
                        {
                            authorizationValidUntil = u.LastDirectorySyncAt?.AddMinutes(maxStaleness);
                            authorizationCurrent = authorizationValidUntil > DateTime.UtcNow;
                        }

                        var remaining = authorizationValidUntil - DateTime.UtcNow;
                        entry.AbsoluteExpirationRelativeToNow = !authorizationCurrent
                            ? TimeSpan.FromMilliseconds(1)
                            : remaining <= TimeSpan.Zero
                            ? TimeSpan.FromMilliseconds(1)
                            : remaining < CacheTtl ? remaining : CacheTtl;
                    }
                    return new UserStateSnapshot(
                        u.IsActive,
                        u.PasswordChangedAt,
                        u.SecurityStamp,
                        u.Role,
                        u.Provider,
                        u.IsTombstoned,
                        u.LastDirectorySyncAt,
                        u.DirectorySyncStatus,
                        authorizationCurrent,
                        authorizationValidUntil);
                });

                if (userState is null || !userState.IsActive || userState.IsTombstoned)
                {
                    await RejectOrAnonymizeAsync(ctx, allowAnonymous);
                    return;
                }

                if (userState.Provider != AuthProvider.Local
                    && !userState.ExternalAuthorizationCurrent)
                {
                    await RejectOrAnonymizeAsync(ctx, allowAnonymous);
                    return;
                }

                // H-1 (security audit 2026-05-15): SecurityStamp comparison. The JWT carries
                // the stamp it was minted with; if the row has since been bumped (role change,
                // active toggle), the token is stale and must be rejected — otherwise a
                // demoted Admin keeps their Admin claim until the 12h token expires.
                // np_secstamp is mandatory for NodePilot sessions; legacy tokens fail closed.
                if (requiredStamp != userState.SecurityStamp)
                {
                    await RejectOrAnonymizeAsync(ctx, allowAnonymous);
                    return;
                }

                // Defense in depth for signing-key exposure: a forged JWT can copy a valid
                // session id, jti and security stamp from the attacker's own session. It must
                // still not be able to replace Viewer with Admin in the signed role claim.
                // Role changes normally bump SecurityStamp; this direct comparison also covers
                // legacy/manual DB edits that failed to perform that bump.
                if (requiredRole != userState.Role)
                {
                    await RejectOrAnonymizeAsync(ctx, allowAnonymous);
                    return;
                }

                // Password-change invalidation (security-audit finding H-3): if the token
                // was issued before the user's current password was set, reject it.
                // Admin-reset of a compromised user's password therefore kicks every
                // existing session for that user without having to enumerate and revoke
                // individual jtis.
                //
                // Precision handling (security-audit finding H13): prefer the
                // NodePilot-specific np_iat_ms claim (millisecond precision) over the
                // RFC-standard iat (second precision). With
                // ms precision we can compare directly against PasswordChangedAt without a
                // cushion — previously an attacker racing /auth/refresh during an admin
                // password reset could land in the same wall-clock second as the reset and
                // the 1-second cushion would incorrectly let the new token through.
                DateTime? iatUtc = null;
                var iatMsStr = ctx.User.FindFirstValue("np_iat_ms");
                if (long.TryParse(iatMsStr, out var iatMs))
                    iatUtc = DateTimeOffset.FromUnixTimeMilliseconds(iatMs).UtcDateTime;
                else
                {
                    var iatStr = ctx.User.FindFirstValue(JwtRegisteredClaimNames.Iat);
                    if (long.TryParse(iatStr, out var iatSec))
                        iatUtc = DateTimeOffset.FromUnixTimeSeconds(iatSec).UtcDateTime;
                }

                if (iatUtc is { } issuedAt && issuedAt < userState.PasswordChangedAt)
                {
                    await RejectOrAnonymizeAsync(ctx, allowAnonymous);
                    return;
                }
            }
        }
        await _next(ctx);
    }

    private static Task RejectAsync(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync("{\"message\":\"Token is no longer valid\"}");
    }

    private async Task RejectOrAnonymizeAsync(HttpContext ctx, bool allowAnonymous)
    {
        if (!allowAnonymous)
        {
            await RejectAsync(ctx);
            return;
        }

        // Login, logout and public bootstrap endpoints must remain usable when the browser
        // presents an expired cookie. Strip the invalid identity instead of trusting it or
        // blocking the anonymous operation. Preserve the already signature-validated raw
        // principal only in HttpContext.Items so logout can still revoke its server-side
        // session family; authorization and every other endpoint see an anonymous user.
        ctx.Items[InvalidatedPrincipalItem] = ctx.User;
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity());
        await _next(ctx);
    }

    // Immutable snapshot stored in the cache so we don't accidentally hand out a tracked
    // EF entity across request scopes.
    private sealed record UserStateSnapshot(
        bool IsActive,
        DateTime PasswordChangedAt,
        int SecurityStamp,
        UserRole Role,
        AuthProvider Provider,
        bool IsTombstoned,
        DateTime? LastDirectorySyncAt,
        string? DirectorySyncStatus,
        bool ExternalAuthorizationCurrent,
        DateTime? AuthorizationValidUntil);
}
