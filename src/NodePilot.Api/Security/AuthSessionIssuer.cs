using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Api.Security;

/// <summary>
/// Default <see cref="IAuthSessionIssuer"/>. Drop-in replacement for the previously
/// private <c>AuthController.SetAuthCookies</c> / <c>AuthController.GenerateJwtToken</c>
/// pair — same JWT shape, same cookie flags, same audit envelope — so that the
/// existing local-login flow is unchanged and the new LDAP / Windows-Auth flows can
/// reuse exactly this issuer instead of duplicating the mechanics.
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

    // Optional IHostEnvironment is null-default so existing test sites (10 fixtures across
    // AuthControllerLdap*Tests / AuthControllerWindowsTests / AuthControllerMethodsTests)
    // keep their `new AuthSessionIssuer(cfg, key, audit)` shape. In real DI the container
    // always resolves IHostEnvironment from the host, so production behavior is unchanged.
    // The null fallback reproduces the legacy "Secure depends on Request.IsHttps"-path that
    // tests rely on, while a non-Development env enforces Secure=true on the cookie pair.
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
        // Audit-trail explicitly names the auth source. Local-password logins keep
        // the old "LOGIN_SUCCESS" action so existing dashboards and SIEM rules don't
        // need to change shape; we add the structured "source" field on top of the
        // historical username + role details.
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

        if (_db is not null)
        {
            AuthSession? persisted = null;
            if (isRefresh
                && Guid.TryParse(httpContext.User.FindFirstValue(SessionIdClaim), out var currentSessionId))
            {
                persisted = await _db.AuthSessions
                    .FirstOrDefaultAsync(s => s.Id == currentSessionId && s.UserId == user.Id, ct);
                if (persisted is null || persisted.RevokedAt is not null || persisted.ExpiresAt <= now.UtcDateTime)
                    throw new UnauthorizedAccessException("The authentication session is no longer active.");
                var presentedJti = httpContext.User.FindFirstValue(JwtRegisteredClaimNames.Jti);
                if (string.IsNullOrEmpty(presentedJti)
                    || !string.Equals(persisted.CurrentJti, presentedJti, StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("The authentication token was already rotated.");

                sessionId = persisted.Id;
                expiresAt = new DateTimeOffset(DateTime.SpecifyKind(persisted.ExpiresAt, DateTimeKind.Utc));
                authMethod = persisted.AuthenticationMethod;
                persisted.LastSeenAt = now.UtcDateTime;
                persisted.AuthorizationVersion = user.SecurityStamp;
                persisted.CurrentJti = tokenJti;
                persisted.RefreshGeneration++;
            }
            else
            {
                persisted = new AuthSession
                {
                    Id = sessionId,
                    UserId = user.Id,
                    AuthenticationMethod = authMethod,
                    CreatedAt = now.UtcDateTime,
                    LastSeenAt = now.UtcDateTime,
                    ExpiresAt = expiresAt.UtcDateTime,
                    AuthorizationVersion = user.SecurityStamp,
                    CurrentJti = tokenJti,
                };
                _db.AuthSessions.Add(persisted);
            }

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException ex) when (isRefresh)
            {
                throw new UnauthorizedAccessException("The authentication token was already rotated.", ex);
            }
        }

        var token = GenerateJwtToken(user, now, expiresAt, sessionId, tokenJti);
        // Tests construct the controller without a ControllerContext when they only need
        // to assert response shape — same behaviour as the legacy AuthController.SetAuthCookies
        // which silently skipped cookies when HttpContext was null.
        if (httpContext is not null)
            SetAuthCookies(httpContext, token, expiresAt, _environment);
        return new IssuedSession(token, user.Id, expiresAt);
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

        // Identical baseline claim shape to the legacy AuthController.GenerateJwtToken so
        // existing tokens + middleware (TokenValidityMiddleware, RevokedTokens) stay
        // compatible. np_iat_ms gives ms precision for the password-change race-guard
        // (audit H13). Deliberately NO group claims: directory group SIDs stay server-side
        // in DirectoryMemberships, and the group-aware ResourceAuthorizationService reads
        // them per request — a token can therefore never carry stale group authorization.
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
