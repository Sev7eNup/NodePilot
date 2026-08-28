using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NodePilot.Core.Audit;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Api.Security;

/// <summary>
/// Default <see cref="IAuthSessionIssuer"/> shared by local, LDAP, and Windows authentication.
/// Centralizes JWT, cookie, and audit behavior so all login paths apply the same policy.
/// </summary>
public sealed class AuthSessionIssuer : IAuthSessionIssuer
{
    private const string AuthCookieName = "np_auth";
    private const string CsrfCookieName = "np_csrf";
    internal const string SessionIdClaim = "np_session";

    private readonly IConfiguration _config;
    private readonly IJwtKeyProvider _keyProvider;
    private readonly IAuditWriter _audit;
    private readonly IHostEnvironment? _environment;
    private readonly NodePilotDbContext? _db;
    private readonly AuthenticationPolicyOptions _policy;

    private sealed record RefreshRotationAttempt(
        Guid UserId,
        int AuthorizationVersion,
        string PresentedJti,
        string NewJti,
        Guid SessionId,
        bool HasServerSession,
        DateTimeOffset AttemptedAt,
        DateTimeOffset FallbackExpiresAt,
        DateTime PresentedTokenExpiresAt,
        string FallbackAuthenticationMethod)
    {
        public DateTimeOffset CommittedExpiresAt { get; set; } = FallbackExpiresAt;
    }

    // A null environment lets isolated tests derive cookie security from Request.IsHttps.
    // Hosted environments enforce secure cookies outside development.
    public AuthSessionIssuer(
        IConfiguration config,
        IJwtKeyProvider keyProvider,
        IAuditWriter audit,
        IHostEnvironment? environment = null,
        NodePilotDbContext? db = null,
        IOptions<AuthenticationPolicyOptions>? policy = null)
    {
        _config = config;
        _keyProvider = keyProvider;
        _audit = audit;
        _environment = environment;
        _db = db;
        _policy = policy?.Value ?? new AuthenticationPolicyOptions();
    }

    public async Task<IssuedSession> IssueAsync(User user, AuthSource source, HttpContext httpContext, CancellationToken ct)
    {
        var session = await MintAndSetCookiesAsync(user, source, httpContext, isRefresh: false, ct);
        // Preserve the LOGIN_SUCCESS action consumed by dashboards and SIEM rules.
        // The structured source field distinguishes authentication methods.
        var breakGlassLogin = source == AuthSource.Local && user.IsBreakGlass;
        await _audit.LogAsync(
            breakGlassLogin ? AuditActions.BreakGlassLoginSuccess : AuditActions.LoginSuccess,
            "User",
            user.Id,
            AuditDetails.Json(
                ("username", user.Username),
                ("role", user.Role.ToString()),
                ("source", source.ToString()),
                ("breakGlass", breakGlassLogin)),
            ct);
        return session;
    }

    public Task<IssuedSession> RefreshAsync(User user, HttpContext httpContext, CancellationToken ct)
    {
        // Refresh = JWT-mint + cookie-rotation. The audit row is emitted by the caller
        // (AuthController.Refresh) with a distinct TOKEN_REFRESHED action — distinct from
        // LOGIN_SUCCESS so dashboards that count active logins are not double-counted by
        // 12h-cadence rotations, but still forensically visible (a stolen-and-renewed
        // token leaves a trail). The issuer itself stays audit-free so the IssueAsync /
        // RefreshAsync pair remains symmetric (issuer = mechanics, caller = semantics).
        return MintAndSetCookiesAsync(user, source: null, httpContext, isRefresh: true, ct);
    }

    private async Task<IssuedSession> MintAndSetCookiesAsync(
        User user,
        AuthSource? source,
        HttpContext httpContext,
        bool isRefresh,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var configuredHours = Math.Clamp(_policy.SessionAbsoluteLifetimeHours, 1, 24 * 7);
        var expiresAt = now.AddHours(configuredHours);
        var sessionId = Guid.NewGuid();
        var tokenJti = Guid.NewGuid().ToString("N");
        var authMethod = source?.ToString() ?? AuthSource.Local.ToString();
        var tokenRotationCommitted = false;

        if (_db is not null)
        {
            if (isRefresh)
            {
                var presentedJti = httpContext.User.FindFirstValue(JwtRegisteredClaimNames.Jti);
                if (string.IsNullOrEmpty(presentedJti))
                    throw new UnauthorizedAccessException("The authentication token has no identifier.");

                var hasServerSession = Guid.TryParse(
                    httpContext.User.FindFirstValue(SessionIdClaim), out var currentSessionId);
                if (hasServerSession) sessionId = currentSessionId;
                long.TryParse(httpContext.User.FindFirstValue("exp"), out var expSeconds);
                var presentedTokenExpiresAt = expSeconds > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(expSeconds).UtcDateTime
                    : expiresAt.UtcDateTime;
                var attempt = new RefreshRotationAttempt(
                    user.Id,
                    user.SecurityStamp,
                    presentedJti,
                    tokenJti,
                    sessionId,
                    hasServerSession,
                    now,
                    expiresAt,
                    presentedTokenExpiresAt,
                    authMethod);

                expiresAt = await PersistRefreshRotationAsync(_db, attempt, ct);
                tokenRotationCommitted = true;
            }
            else
            {
                _db.AuthSessions.Add(new AuthSession
                {
                    Id = sessionId,
                    UserId = user.Id,
                    AuthenticationMethod = authMethod,
                    CreatedAt = now.UtcDateTime,
                    LastSeenAt = now.UtcDateTime,
                    ExpiresAt = expiresAt.UtcDateTime,
                    AuthorizationVersion = user.SecurityStamp,
                    CurrentJti = tokenJti,
                });
                await _db.SaveChangesAsync(ct);
            }
        }

        var token = GenerateJwtToken(user, now, expiresAt, sessionId, tokenJti);
        // A null context supports response-only unit tests that do not inspect cookies.
        if (httpContext is not null)
            SetAuthCookies(httpContext, token, expiresAt, _environment);
        return new IssuedSession(token, user.Id, expiresAt, tokenRotationCommitted);
    }

    private static async Task<DateTimeOffset> PersistRefreshRotationAsync(
        NodePilotDbContext db,
        RefreshRotationAttempt attempt,
        CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteInTransactionAsync(
                attempt,
                async (state, token) =>
                {
                    // Every retry starts from committed database state. This is essential
                    // after a lost COMMIT acknowledgement: EF may already have accepted the
                    // first attempt's tracked entities even though the strategy must verify it.
                    db.ChangeTracker.Clear();
                    AuthSession persisted;
                    if (state.HasServerSession)
                    {
                        persisted = await db.AuthSessions.FirstOrDefaultAsync(
                                session => session.Id == state.SessionId
                                           && session.UserId == state.UserId,
                                token)
                            ?? throw new UnauthorizedAccessException(
                                "The authentication session is no longer active.");
                        if (persisted.RevokedAt is not null
                            || persisted.ExpiresAt <= state.AttemptedAt.UtcDateTime)
                        {
                            throw new UnauthorizedAccessException(
                                "The authentication session is no longer active.");
                        }
                        if (!string.Equals(
                                persisted.CurrentJti, state.PresentedJti, StringComparison.Ordinal))
                        {
                            throw new UnauthorizedAccessException(
                                "The authentication token was already rotated.");
                        }

                        state.CommittedExpiresAt = new DateTimeOffset(
                            DateTime.SpecifyKind(persisted.ExpiresAt, DateTimeKind.Utc));
                        persisted.LastSeenAt = state.AttemptedAt.UtcDateTime;
                        persisted.AuthorizationVersion = state.AuthorizationVersion;
                        persisted.CurrentJti = state.NewJti;
                        persisted.RefreshGeneration++;
                    }
                    else
                    {
                        persisted = new AuthSession
                        {
                            Id = state.SessionId,
                            UserId = state.UserId,
                            AuthenticationMethod = state.FallbackAuthenticationMethod,
                            CreatedAt = state.AttemptedAt.UtcDateTime,
                            LastSeenAt = state.AttemptedAt.UtcDateTime,
                            ExpiresAt = state.FallbackExpiresAt.UtcDateTime,
                            AuthorizationVersion = state.AuthorizationVersion,
                            CurrentJti = state.NewJti,
                        };
                        state.CommittedExpiresAt = state.FallbackExpiresAt;
                        db.AuthSessions.Add(persisted);
                    }

                    if (await db.RevokedTokens.AsNoTracking().AnyAsync(
                            revoked => revoked.Jti == state.PresentedJti, token))
                    {
                        throw new UnauthorizedAccessException(
                            "The authentication token was already rotated.");
                    }
                    db.RevokedTokens.Add(new RevokedToken
                    {
                        Jti = state.PresentedJti,
                        UserId = state.UserId,
                        RevokedAt = state.AttemptedAt.UtcDateTime,
                        ExpiresAt = state.PresentedTokenExpiresAt,
                        Reason = "rotated",
                    });

                    await db.SaveChangesAsync(token);
                },
                async (state, token) => await VerifyRefreshRotationAsync(db, state, token),
                IsolationLevel.ReadCommitted,
                ct);
            return attempt.CommittedExpiresAt;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            if (await VerifyRefreshRotationAsync(db, attempt, ct))
                return attempt.CommittedExpiresAt;
            throw new UnauthorizedAccessException(
                "The authentication token was already rotated.", ex);
        }
        catch (DbUpdateException ex) when (DbErrorClassifier.IsUniqueConstraintViolation(ex))
        {
            if (await VerifyRefreshRotationAsync(db, attempt, ct))
                return attempt.CommittedExpiresAt;
            throw new UnauthorizedAccessException(
                "The authentication token was already rotated.", ex);
        }
        catch (DbUpdateException ex)
        {
            if (await VerifyRefreshRotationAsync(db, attempt, ct))
                return attempt.CommittedExpiresAt;
            if (await PresentedTokenWasRotatedAsync(db, attempt, ct))
            {
                throw new UnauthorizedAccessException(
                    "The authentication token was already rotated.", ex);
            }
            throw;
        }
    }

    private static async Task<bool> VerifyRefreshRotationAsync(
        NodePilotDbContext db,
        RefreshRotationAttempt attempt,
        CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var committedSession = await db.AuthSessions.AsNoTracking()
            .Where(session => session.Id == attempt.SessionId
                              && session.UserId == attempt.UserId
                              && session.CurrentJti == attempt.NewJti)
            .Select(session => new { session.ExpiresAt })
            .FirstOrDefaultAsync(ct);
        if (committedSession is null) return false;
        if (!await db.RevokedTokens.AsNoTracking()
                .AnyAsync(revoked => revoked.Jti == attempt.PresentedJti, ct))
        {
            return false;
        }

        attempt.CommittedExpiresAt = new DateTimeOffset(
            DateTime.SpecifyKind(committedSession.ExpiresAt, DateTimeKind.Utc));
        return true;
    }

    private static async Task<bool> PresentedTokenWasRotatedAsync(
        NodePilotDbContext db,
        RefreshRotationAttempt attempt,
        CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        return await db.RevokedTokens.AsNoTracking()
            .AnyAsync(revoked => revoked.Jti == attempt.PresentedJti, ct);
    }

    private string GenerateJwtToken(
        User user,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        Guid sessionId,
        string tokenJti)
    {
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_keyProvider.Key));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        // Keep the claim shape compatible with token middleware and revocation checks.
        // np_iat_ms provides millisecond precision for the password-change race guard.
        // Directory group SIDs remain server-side to prevent stale token authorization.
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(JwtRegisteredClaimNames.Jti, tokenJti),
            new(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64),
            new("np_iat_ms", now.ToUnixTimeMilliseconds().ToString(),
                ClaimValueTypes.Integer64),
            new(SessionIdClaim, sessionId.ToString("D")),
            // H-1 (security audit 2026-05-15): SecurityStamp pinned at mint time. The
            // TokenValidityMiddleware re-reads the current value from the DB on every
            // request and rejects any token whose stamp does not match — so a role demote
            // or account deactivation invalidates all existing sessions immediately
            // instead of waiting for the 12h JWT lifetime.
            new("np_secstamp", user.SecurityStamp.ToString(), ClaimValueTypes.Integer32),
        };

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"] ?? "NodePilot",
            audience: _config["Jwt:Audience"] ?? "NodePilot",
            claims: claims,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static void SetAuthCookies(HttpContext httpContext, string jwt, DateTimeOffset expiresAt,
        IHostEnvironment? environment)
    {
        // L-1a (security audit 2026-05-15): cookie flags are routed through
        // AuthCookieOptionsBuilder so the set-path and the clear-path
        // (AuthController.ClearAuthCookies) produce IDENTICAL Secure/SameSite/Path.
        // Outside Development, Secure is always true (production deployments terminate
        // TLS directly on Kestrel); in dev mode Secure mirrors whether the request itself
        // is HTTPS, so `dotnet run --urls http://...` keeps working.
        //
        // Random 256-bit CSRF token — regenerated on every login/refresh so token rotation
        // and CSRF rotation travel together.
        var csrf = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        httpContext.Response.Cookies.Append(AuthCookieName, jwt,
            AuthCookieOptionsBuilder.ForAuth(httpContext, environment, expiresAt));
        httpContext.Response.Cookies.Append(CsrfCookieName, csrf,
            AuthCookieOptionsBuilder.ForCsrf(httpContext, environment, expiresAt));
    }
}
