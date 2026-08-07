using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NodePilot.Api.Hosting;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Availability;

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

    /// <summary>
    /// Named rather than anonymous so the query can sit inside its own command-budget block while the
    /// result stays usable in the enclosing scope — an anonymous type cannot be declared ahead of the
    /// block that produces it.
    /// </summary>
    private sealed record ActiveSessionProjection(
        DateTime? RevokedAt, DateTime ExpiresAt, int AuthorizationVersion, string CurrentJti);

    private readonly RequestDelegate _next;
    private readonly int _authReadTimeoutSeconds;

    public TokenValidityMiddleware(
        RequestDelegate next,
        DatabaseAvailabilityOptions databaseAvailability)
    {
        _next = next;
        _authReadTimeoutSeconds = databaseAvailability.AuthReadTimeoutSeconds;
    }

    public async Task Invoke(
        HttpContext ctx,
        NodePilotDbContext db,
        IMemoryCache cache,
        IOptions<AuthenticationPolicyOptions>? authenticationPolicy = null,
        ExternalAuthorizationEvaluator? externalAuthorization = null)
    {
        // The health endpoints are the one surface that must answer while the database is in trouble -
        // /healthz/database is what the SPA polls to learn an outage is happening. Running the two
        // uncached reads below first would make the outage banner's own probe pay a full auth budget
        // per tick, which is the chicken-and-egg that endpoint exists to break. No health endpoint
        // reads ctx.User.
        if (ctx.Request.Path.StartsWithSegments("/healthz"))
        {
            await _next(ctx);
            return;
        }

        // DatabaseAvailabilityMiddleware runs before authentication and marks an authenticated
        // browser navigation while the breaker is open. Authentication has populated ctx.User by the
        // time we get here, so strip it and serve the static SPA shell without touching RevokedTokens,
        // AuthSessions or Users. API, OIDC, metrics and the entire hub HTTP surface are sealed by the
        // outer middleware and can never reach this branch.
        if (ctx.Items.ContainsKey(
                Hosting.DatabaseAvailabilityMiddleware.AnonymizeSpaPrincipalItem))
        {
            if (ctx.User?.Identity?.IsAuthenticated == true)
                await RejectOrAnonymizeAsync(ctx, allowAnonymous: true);
            else
                await _next(ctx);
            return;
        }

        // Why this exists at all: these reads run on EVERY authenticated request, before any
        // controller. At the 120 s context default, a hung database parks each of them for
        // 120 s + 2 x connect timeout - so the interactive budgets controllers set for themselves are
        // never even reached. A short budget here is what makes a command timeout - and therefore the
        // probe arming - happen in seconds instead of minutes. It comes from the same immutable,
        // startup-validated DatabaseAvailabilityOptions snapshot as every other breaker budget.

        var endpoint = ctx.GetEndpoint();
        // Anonymize instead of reject on everything that is not the protected API surface.
        // The SPA fallback endpoint (index.html for /login, /workflows, …) carries no
        // [AllowAnonymous] metadata, so a browser holding an expired/revoked np_auth cookie
        // otherwise gets this middleware's raw 401 JSON instead of the page — including on
        // /login itself, leaving no way back in short of manually clearing cookies
        // (lab 2026-08-01: SessionAbsoluteLifetimeHours elapsed overnight → site "bricked").
        // Dev never sees this because Vite serves the shell and only proxies /api. Stripping
        // the stale identity is safe here: static files and the SPA shell never consume it,
        // and the SPA's own /auth/me probe lands on the API surface below, where invalid
        // tokens still hard-fail.
        var allowAnonymous = endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is not null
            || !(ctx.Request.Path.StartsWithSegments("/api")
                 || ctx.Request.Path.StartsWithSegments("/hubs"));

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
                    // Scoped per call rather than around the whole branch: RejectOrAnonymizeAsync calls
                    // _next on its anonymize path, and a budget that spanned it would silently run the
                    // entire downstream pipeline at 3 seconds. The context is pooled, so a leaked
                    // override would outlive the request and poison an unrelated one later.
                    //
                    // The catch below (and its two siblings further down) closes a hole the outage
                    // banner depends on: `/`, `/login` and every other SPA route are allowAnonymous
                    // here, but an authenticated browser still triggers these reads — and an
                    // unhandled database failure would hand the DOCUMENT request to the exception
                    // handler, which answers 503 JSON. The user reloading during an outage then saw
                    // raw JSON instead of the app shell, and the banner that explains the outage
                    // could never render. Anonymize-and-continue serves the shell; the API surface
                    // (allowAnonymous == false) deliberately keeps propagating so the handlers can
                    // answer with the DATABASE_UNAVAILABLE contract. Narrow catches on purpose: a
                    // catch spanning RejectOrAnonymizeAsync would swallow downstream pipeline
                    // exceptions and run _next twice.
                    try
                    {
                        using (DatabaseCommandBudget.Apply(db, _authReadTimeoutSeconds))
                        {
                            revoked = await db.RevokedTokens.AsNoTracking()
                                .AnyAsync(r => r.Jti == jti, ctx.RequestAborted);
                        }
                    }
                    catch (Exception ex) when (allowAnonymous
                        && DbErrorClassifier.Classify(ex) is not DbFailureKind.None)
                    {
                        await RejectOrAnonymizeAsync(ctx, allowAnonymous: true);
                        return;
                    }
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
                    // Block-scoped, NOT `using var`: the enclosing block contains the
                    // RejectOrAnonymizeAsync calls below, which invoke _next on their anonymize path.
                    // A `using var` here would run the whole downstream pipeline at the auth budget.
                    ActiveSessionProjection? activeSession;
                    try
                    {
                        using (DatabaseCommandBudget.Apply(db, _authReadTimeoutSeconds))
                        {
                            activeSession = await db.AuthSessions.AsNoTracking()
                                .Where(s => s.Id == sessionId && s.UserId == userId)
                                .Select(s => new ActiveSessionProjection(
                                    s.RevokedAt, s.ExpiresAt, s.AuthorizationVersion, s.CurrentJti))
                                .FirstOrDefaultAsync(ctx.RequestAborted);
                        }
                    }
                    catch (Exception ex) when (allowAnonymous
                        && DbErrorClassifier.Classify(ex) is not DbFailureKind.None)
                    {
                        // See the revocation read above: the SPA shell must load during an outage.
                        await RejectOrAnonymizeAsync(ctx, allowAnonymous: true);
                        return;
                    }
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
                UserStateSnapshot? userState;
                try
                {
                    userState = await cache.GetOrCreateAsync(userKey, async entry =>
                    {
                    entry.AbsoluteExpirationRelativeToNow = CacheTtl;
                    User? found;
                    using (DatabaseCommandBudget.Apply(db, _authReadTimeoutSeconds))
                    {
                        found = await db.Users.AsNoTracking()
                            .FirstOrDefaultAsync(x => x.Id == userId, ctx.RequestAborted);
                    }
                    if (found is null) return null;
                    var u = found;
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
                }
                catch (Exception ex) when (allowAnonymous
                    && DbErrorClassifier.Classify(ex) is not DbFailureKind.None)
                {
                    // Third sibling of the revocation-read catch: the user lookup inside the cache
                    // factory surfaces its database failure from THIS await.
                    await RejectOrAnonymizeAsync(ctx, allowAnonymous: true);
                    return;
                }

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
