using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Api.Dtos;
using NodePilot.Api.Security;
using NodePilot.Api.Security.Ldap;
using NodePilot.Api.Telemetry;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(8);

    // BCrypt work factor — 12 gives ~250ms per hash on 2026 hardware, balancing brute-force
    // resistance against login latency. Raise when hardware gets meaningfully faster.
    internal const int BCryptWorkFactor = 12;

    // Precomputed hash used to burn the same CPU cycles on unknown-username logins as on
    // real ones, so response time does not leak which usernames exist.
    private static readonly string DummyHash =
        BCrypt.Net.BCrypt.HashPassword("dummy-password-for-timing-equalization", BCryptWorkFactor);

    // Minimum password length at first-admin bootstrap. The UsersController enforces 8 for
    // every other user; historically the bootstrap branch had NO length check, so a rushed
    // operator could ship with `password=a`. Match the rest of the system (L1).
    internal const int MinPasswordLength = 8;

    // BCrypt silently truncates inputs longer than 72 bytes, so "A*72 + X" and "A*72 + Y"
    // hash to the same value. Reject overly long passwords rather than let the user believe
    // they have a stronger secret than they actually do (L1).
    internal const int MaxPasswordBytes = 72;

    private readonly NodePilotDbContext _db;
    private readonly IConfiguration _config;
    private readonly IAuditWriter _audit;
    private readonly NodePilot.Api.Security.IJwtKeyProvider _keyProvider;
    private readonly NodePilot.Api.Security.IAuthSessionIssuer _sessionIssuer;
    // L-1a: optional IHostEnvironment so cookie Secure-flag matches the set-path. Null in
    // tests that don't supply it — the builder falls back to Request.IsHttps which matches
    // the legacy dev-mode behaviour those tests expect.
    private readonly IHostEnvironment? _environment;
    // PR4: LDAP services are optional — null when the LDAP feature is not registered (e.g.,
    // the slim test ctor below). Production wiring always supplies them.
    private readonly LdapAuthenticator? _ldapAuthenticator;
    private readonly ExternalUserMapper? _externalUserMapper;
    private readonly IOptionsMonitor<LdapOptions>? _ldapOptions;
    private readonly IOptionsMonitor<WindowsAuthOptions>? _windowsOptions;
    private readonly ActiveAuthenticationConfiguration _activeAuthentication;
    private readonly AuthenticationPolicyOptions _authenticationPolicy;
    private readonly ExternalLoginThrottle _externalLoginThrottle;
    private readonly ActiveDirectoryAuthenticationConfiguration? _activeDirectoryAuthentication;
    private readonly ILdapConnectionAdapter? _directoryAdapter;

    public AuthController(NodePilotDbContext db, IConfiguration config, IAuditWriter audit,
        NodePilot.Api.Security.IJwtKeyProvider keyProvider,
        NodePilot.Api.Security.IAuthSessionIssuer sessionIssuer,
        LdapAuthenticator? ldapAuthenticator = null,
        ExternalUserMapper? externalUserMapper = null,
        IOptionsMonitor<LdapOptions>? ldapOptions = null,
        IOptionsMonitor<WindowsAuthOptions>? windowsOptions = null,
        IHostEnvironment? environment = null,
        ActiveAuthenticationConfiguration? activeAuthentication = null,
        IOptions<AuthenticationPolicyOptions>? authenticationPolicy = null,
        ExternalLoginThrottle? externalLoginThrottle = null,
        ActiveDirectoryAuthenticationConfiguration? activeDirectoryAuthentication = null,
        ILdapConnectionAdapter? directoryAdapter = null)
    {
        _db = db;
        _config = config;
        _audit = audit;
        _keyProvider = keyProvider;
        _sessionIssuer = sessionIssuer;
        _ldapAuthenticator = ldapAuthenticator;
        _externalUserMapper = externalUserMapper;
        _ldapOptions = ldapOptions;
        _windowsOptions = windowsOptions;
        _environment = environment;
        _authenticationPolicy = authenticationPolicy?.Value ?? new AuthenticationPolicyOptions();
        // Production always injects the immutable startup snapshot. Direct unit-test
        // construction predates that service; preserve its explicit option monitors and
        // legacy local-enabled intent without weakening the hosted path.
        _activeAuthentication = activeAuthentication ?? new ActiveAuthenticationConfiguration(
            LocalLoginMode.Enabled,
            ldapOptions?.CurrentValue.Enabled ?? config.GetValue<bool>("Authentication:Ldap:Enabled"),
            windowsOptions?.CurrentValue.Enabled
                ?? (externalUserMapper is not null || config.GetValue<bool>("Authentication:Windows:Enabled")),
            config.GetValue<bool>("Authentication:Oidc:Enabled"),
            config["Authentication:Oidc:DisplayName"] ?? "Single Sign-On");
        _externalLoginThrottle = externalLoginThrottle ?? new ExternalLoginThrottle(db);
        _activeDirectoryAuthentication = activeDirectoryAuthentication;
        _directoryAdapter = directoryAdapter;
    }

    // H-4: account-lockout thresholds. Ten consecutive failures in 15 minutes locks the
    // account for 15 minutes. The fixed window is short enough that legit fat-fingered users
    // recover quickly and long enough that distributed-brute-force attackers burn too much
    // wall-clock to be productive. Counts reset on a successful login.
    internal const int LockoutFailureThreshold = 10;
    internal static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    // H-5: cookie names for the httpOnly auth cookie and the (JS-readable) CSRF token.
    // The SPA no longer persists the JWT in localStorage where a single XSS bug would
    // exfiltrate an admin token; the browser holds it in an httpOnly cookie and the
    // double-submit CSRF token prevents cross-origin form submission abuse.
    internal const string AuthCookieName = "np_auth";
    internal const string CsrfCookieName = "np_csrf";

    // H-5 completion: the JWT must never reach a browser response body, or an XSS could
    // exfiltrate a portable 12h bearer token via /auth/refresh and defeat the httpOnly-cookie
    // design entirely. We hand the token back ONLY to provably non-browser callers, using two
    // different discriminators because the two situations differ:
    //
    //   - Login / LDAP (password-gated): an explicit opt-in header. Safe here because no caller
    //     reaches a 200 without valid credentials — an XSS has no password, so it can never
    //     trigger a token-bearing response no matter what headers it sets.
    //   - Refresh (cookie- or Bearer-authenticated): a *real* Authorization: Bearer header.
    //     This is un-spoofable by an XSS — it cannot read the httpOnly np_auth cookie, so it
    //     cannot construct a valid Bearer header. Refresh therefore deliberately does NOT
    //     honour the opt-in header: a cookie-authenticated request (browser, or XSS riding the
    //     browser's cookie) always gets identity-only, even if it sets X-Auth-Token-Response.
    internal const string TokenResponseHeader = "X-Auth-Token-Response";

    /// <summary>Login / LDAP opt-in: caller explicitly requested the token in the body. Only
    /// meaningful on password-gated paths (see class-level comment). Null-safe: a controller
    /// constructed without an HttpContext (unit tests) counts as "no opt-in" → identity-only.</summary>
    private bool TokenInBodyRequested()
    {
        var ctx = HttpContext;
        return ctx is not null
            && ctx.Request.Headers.TryGetValue(TokenResponseHeader, out var v)
            && string.Equals(v.ToString(), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Refresh discriminator: true only when the caller authenticated via a genuine
    /// <c>Authorization: Bearer</c> header — something an XSS cannot forge.</summary>
    private bool AuthenticatedViaBearerHeader()
    {
        var ctx = HttpContext;
        return ctx is not null
            && ctx.Request.Headers.TryGetValue("Authorization", out var v)
            && v.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Builds the auth success body: <see cref="LoginResponse"/> (with JWT) for
    /// programmatic callers, <see cref="AuthIdentityResponse"/> (identity only) for browsers.</summary>
    private OkObjectResult SessionResult(string token, User user, bool includeToken)
        => includeToken
            ? Ok(new LoginResponse(token, user.Id, user.Username, user.Role.ToString()))
            : Ok(new AuthIdentityResponse(user.Id, user.Username, user.Role.ToString()));

    /// <summary>
    /// Sets the auth + CSRF cookies for the current response. The JWT lands in an httpOnly
    /// cookie so script code (and any XSS) cannot read it. The CSRF token is readable by JS
    /// on purpose — the SPA reflects it back in the <c>X-CSRF-Token</c> header on every
    /// mutating request; a CSRF attacker from another origin cannot read the token because
    /// SameSite=Strict + cross-origin cookie access denies the fetch.
    /// </summary>
    /// <summary>
    /// Expires both cookies. Matches the same Secure/SameSite/Path flags so browsers accept
    /// the overwrite instead of treating it as a different cookie.
    /// </summary>
    private void ClearAuthCookies()
    {
        if (HttpContext is null) return;
        // L-1a (security audit 2026-05-15): the set-path and the clear-path share
        // AuthCookieOptionsBuilder so the Secure / SameSite / Path triple matches.
        // Previously this used Request.IsHttps while AuthSessionIssuer.SetAuthCookies used
        // environment.IsDevelopment() — behind a TLS-terminating proxy with broken
        // ForwardedHeaders the delete-cookie did not match the set-cookie, and the browser
        // kept the stale auth cookie.
        Response.Cookies.Delete(AuthCookieName,
            AuthCookieOptionsBuilder.ForAuth(HttpContext, _environment, DateTimeOffset.UnixEpoch));
        Response.Cookies.Delete(CsrfCookieName,
            AuthCookieOptionsBuilder.ForCsrf(HttpContext, _environment, DateTimeOffset.UnixEpoch));
    }

    /// <summary>
    /// Enforces the shared password policy: non-empty, at least <see cref="MinPasswordLength"/>
    /// characters, and no longer than <see cref="MaxPasswordBytes"/> UTF-8 bytes (BCrypt's
    /// silent-truncation limit). Returns null on success, an error message on failure.
    /// </summary>
    internal static string? ValidatePasswordPolicy(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            return $"password must be at least {MinPasswordLength} characters";
        if (System.Text.Encoding.UTF8.GetByteCount(password) > MaxPasswordBytes)
            return $"password exceeds {MaxPasswordBytes} bytes (BCrypt truncates beyond this, making the extra characters security-irrelevant)";
        return null;
    }

    /// <summary>
    /// L-15: audit-safe rendition of the presented username. The raw request.Username lands
    /// in the LOGIN_FAILED audit details as JSON; without capping + control-char stripping,
    /// a caller could stuff megabytes of attacker-controlled text into the audit log per
    /// failed request (DoS on the audit table, noise in SIEM alerts, potential log-view XSS
    /// via CR/LF/backspace). 64 chars is above any realistic username (local + domain form
    /// tops out well under that) and the control-char filter removes anything in \x00–\x1F
    /// that a reasonable console viewer would interpret as a terminal escape.
    /// </summary>
    private static string SafeUsernameForAudit(string? username)
    {
        var input = username ?? "";
        if (input.Length > 64) input = string.Concat(input.AsSpan(0, 64), "...");
        return System.Text.RegularExpressions.Regex.Replace(input, @"[\x00-\x1F]", "", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
    }

    private async Task<UserLoginReservation> TryReserveUserAttemptAsync(
        User user,
        DateTime utcNow,
        CancellationToken ct)
    {
        var blockedUntil = utcNow.Add(LockoutDuration);
        var updated = await _db.Users
            .Where(u => u.Id == user.Id
                        && (u.LockedUntil == null || u.LockedUntil <= utcNow))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(u => u.FailedLoginCount, u => u.FailedLoginCount + 1)
                .SetProperty(
                    u => u.LockedUntil,
                    u => u.FailedLoginCount + 1 >= LockoutFailureThreshold
                        ? blockedUntil
                        : u.LockedUntil), ct);

        await _db.Entry(user).ReloadAsync(ct);
        if (updated == 0)
            return UserLoginReservation.Blocked(user.LockedUntil);

        var triggeredLockout = user.FailedLoginCount >= LockoutFailureThreshold;
        return UserLoginReservation.Allowed(
            user.FailedLoginCount,
            triggeredLockout,
            triggeredLockout ? user.LockedUntil : null);
    }

    private async Task ResetUserAttemptsAsync(User user, CancellationToken ct)
    {
        await _db.Users.Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(u => u.FailedLoginCount, 0)
                .SetProperty(u => u.LockedUntil, (DateTime?)null), ct);
        await _db.Entry(user).ReloadAsync(ct);
    }

    private async Task ReleaseUserAttemptAsync(
        User user,
        UserLoginReservation reservation,
        CancellationToken ct)
    {
        if (!reservation.IsAllowed) return;

        var lockSetByThisAttempt = reservation.AppliedBlockedUntil;
        await _db.Users
            .Where(u => u.Id == user.Id && u.FailedLoginCount > 0)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(u => u.FailedLoginCount, u => u.FailedLoginCount - 1)
                .SetProperty(
                    u => u.LockedUntil,
                    u => lockSetByThisAttempt != null && u.LockedUntil == lockSetByThisAttempt
                        ? null
                        : u.LockedUntil), ct);
        await _db.Entry(user).ReloadAsync(ct);
    }

    // H-4: account-lockout now uses FailedLoginCount + LockedUntil on User. The branches below
    // reset the counter on success, increment it on password failure, and seal the account for
    // LockoutDuration once the threshold is crossed. Rate-limit-at-IP + BCrypt work factor 12
    // still apply; lockout adds a per-account bucket an attacker cannot rotate past by changing
    // source IP.

    /// <summary>
    /// Anonymous discovery endpoint for the process-start authentication configuration.
    /// Local reflects LocalLoginMode; LDAP, Windows and OIDC are restart-required schemes.
    /// </summary>
    [HttpGet("methods")]
    [AllowAnonymous]
    public ActionResult<AuthMethodsResponse> Methods()
    {
        var ldapEnabled = _activeAuthentication.LdapEnabled;
        var windowsEnabled = _activeAuthentication.WindowsEnabled;
        var oidcEnabled = _activeAuthentication.OidcEnabled;
        return Ok(new AuthMethodsResponse(
            Local: _activeAuthentication.LocalLoginMode != LocalLoginMode.Disabled,
            Ldap: ldapEnabled,
            Windows: windowsEnabled,
            WindowsEndpoint: windowsEnabled ? "/api/auth/windows" : null,
            Oidc: oidcEnabled,
            OidcEndpoint: oidcEnabled ? "/api/auth/oidc" : null,
            OidcDisplayName: oidcEnabled ? _activeAuthentication.OidcDisplayName : null));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        // Reject before LDAP normalization, Unicode case conversion, or DB lookup. Besides
        // matching the persisted User.Username length, this prevents an anonymous caller from
        // using a multi-megabyte JSON string as a CPU/allocation amplifier in the throttle.
        if (string.IsNullOrWhiteSpace(request.Username)
            || request.Username.Length > ExternalLoginThrottle.MaximumUsernameLength)
        {
            _ = BCrypt.Net.BCrypt.Verify(request.Password, DummyHash);
            await _audit.LogAsync(AuditActions.LoginFailed, "User", null,
                AuditDetails.Json(
                    ("username", SafeUsernameForAudit(request.Username)),
                    ("reason", "invalid_username_length")), ct);
            return Unauthorized(new { message = "Invalid credentials" });
        }

        // PR4: LDAP-first when the feature is wired up + enabled. The helper encapsulates
        // the decision tree (Success → mint session, RefusedCollision → 401, InvalidCredentials
        // for known-LDAP user → 401, Unavailable / unknown user → fall through to local).
        var ldapShortCircuit = await TryLdapLoginAsync(request, ct);
        if (ldapShortCircuit is not null) return ldapShortCircuit;

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == request.Username, ct);

        var localMode = _activeAuthentication.LocalLoginMode;
        var bootstrapCandidate = user is null && !await _db.Users.AnyAsync(ct);
        var localAllowed = localMode == LocalLoginMode.Enabled
            || (localMode == LocalLoginMode.BreakGlassOnly
                && (bootstrapCandidate || user is { Provider: AuthProvider.Local, IsBreakGlass: true }));
        if (!localAllowed)
        {
            _ = BCrypt.Net.BCrypt.Verify(request.Password, DummyHash);
            await _audit.LogAsync(AuditActions.LoginFailed, "User", user?.Id,
                AuditDetails.Json(
                    ("username", SafeUsernameForAudit(request.Username)),
                    ("reason", "local_login_policy"),
                    ("mode", localMode.ToString())), ct);
            return Unauthorized(new { message = "Invalid credentials" });
        }

        if (user is null)
        {
            // Auto-create admin user on first login attempt if no users exist — but only
            // when the caller presents the one-shot bootstrap token written by the host on
            // startup. Without this, whoever races to /api/auth/login first becomes Admin.
            if (!await _db.Users.AnyAsync(ct))
            {
                var env = HttpContext.RequestServices.GetRequiredService<IHostEnvironment>();
                var presented = Request.Headers[NodePilot.Api.Security.AdminBootstrap.TokenHeader].ToString();
                if (!NodePilot.Api.Security.AdminBootstrap.Validate(env, presented, _config))
                {
                    _ = BCrypt.Net.BCrypt.Verify(request.Password, DummyHash); // keep timing stable
                    return Unauthorized(new
                    {
                        message = "Admin bootstrap required. Send the X-Setup-Token header " +
                                  "matching the admin-setup.token file written to the content root on first startup."
                    });
                }

                // H12: if the operator has pinned a bootstrap username in config, ONLY that
                // username may consume the setup token. Prevents a token-stealer from
                // choosing the admin username and locking out the rightful operator.
                var pinnedUsername = _config["NodePilot:BootstrapAdminUsername"];
                if (!string.IsNullOrWhiteSpace(pinnedUsername)
                    && !string.Equals(pinnedUsername.Trim(), request.Username, StringComparison.Ordinal))
                {
                    _ = BCrypt.Net.BCrypt.Verify(request.Password, DummyHash);
                    return Unauthorized(new
                    {
                        message = "Admin bootstrap required: the presented setup token is valid but the username does not match NodePilot:BootstrapAdminUsername.",
                    });
                }

                // L1: enforce the password policy during bootstrap too — previously skipped,
                // so a rushed deployment could ship with a one-character admin password.
                if (ValidatePasswordPolicy(request.Password) is { } policyError)
                    return BadRequest(new { message = policyError });

                // H11: serialize empty-check + token revalidation + insert with the existing
                // transaction-scoped admin invariant lock. A process-local gate alone does
                // not protect the one-shot bootstrap contract across HA nodes.
                var bootstrap = await TryCreateBootstrapAdminAsync(
                    request.Username, request.Password, presented, env, ct);
                if (bootstrap.Status == BootstrapAdminCreationStatus.UsersAlreadyExist)
                {
                    _ = BCrypt.Net.BCrypt.Verify(request.Password, DummyHash);
                    return Unauthorized(new { message = "Invalid credentials" });
                }
                if (bootstrap.Status == BootstrapAdminCreationStatus.TokenInvalid)
                {
                    _ = BCrypt.Net.BCrypt.Verify(request.Password, DummyHash);
                    return Unauthorized(new { message = "Admin bootstrap window has closed" });
                }

                user = bootstrap.User!;
                // Close the filesystem window after the database commit. A competing node is
                // still safe if deletion is delayed: under the same DB lock it observes that
                // Users is no longer empty before it can insert another bootstrap Admin.
                NodePilot.Api.Security.AdminBootstrap.Consume(env, _config);

                await _audit.LogAsync(AuditActions.UserCreatedBootstrap, "User", user.Id,
                    AuditDetails.Json(("username", user.Username), ("role", "Admin")), ct);
            }
            else
            {
                var unknownAttempt = await _externalLoginThrottle.TryReserveAsync(
                    "local:" + request.Username, DateTime.UtcNow, ct);
                if (!unknownAttempt.IsAllowed)
                {
                    _ = BCrypt.Net.BCrypt.Verify(request.Password, DummyHash);
                    await _audit.LogAsync(AuditActions.LoginLocked, "User", null,
                        AuditDetails.Json(("username", SafeUsernameForAudit(request.Username)),
                            ("reason", "unknown_local_account_throttle")), ct);
                    return Unauthorized(new { message = "Invalid credentials" });
                }

                // Burn BCrypt work so the response time matches the "valid user, wrong password"
                // branch below — otherwise an attacker can enumerate usernames by timing.
                _ = BCrypt.Net.BCrypt.Verify(request.Password, DummyHash);
                await _audit.LogAsync(AuditActions.LoginFailed, "User", null,
                    AuditDetails.Json(("username", SafeUsernameForAudit(request.Username)), ("reason", "unknown_username")), ct);
                ApiMetrics.AuthLoginAttempts.Add(1,
                    new KeyValuePair<string, object?>("result", "failure"),
                    new KeyValuePair<string, object?>("reason", "unknown_user"));
                return Unauthorized(new { message = "Invalid credentials" });
            }
        }

        // PR1: external-auth users (Provider != Local, or null PasswordHash) cannot log in
        // via the password endpoint — they authenticate against their directory, not against
        // a local hash. We still burn BCrypt on the dummy hash so an attacker cannot fingerprint
        // external accounts by timing.
        if (user.Provider != NodePilot.Core.Enums.AuthProvider.Local || user.PasswordHash is null)
        {
            _ = BCrypt.Net.BCrypt.Verify(request.Password, DummyHash);
            await _audit.LogAsync(AuditActions.LoginFailed, "User", user.Id,
                AuditDetails.Json(
                    ("username", user.Username),
                    ("reason", "external_user_local_login_attempt"),
                    ("provider", user.Provider.ToString())), ct);
            ApiMetrics.AuthLoginAttempts.Add(1,
                new KeyValuePair<string, object?>("result", "failure"),
                new KeyValuePair<string, object?>("reason", "external_user_local_login"));
            return Unauthorized(new { message = "Invalid credentials" });
        }

        // Atomically reserve the attempt before BCrypt. The conditional UPDATE takes the
        // database row lock and closes the old check-then-save race where parallel requests
        // all observed the same failure count and overwrote each other's increments.
        var localAttempt = await TryReserveUserAttemptAsync(user, DateTime.UtcNow, ct);
        if (!localAttempt.IsAllowed)
        {
            _ = BCrypt.Net.BCrypt.Verify(request.Password, DummyHash);
            await _audit.LogAsync(AuditActions.LoginLocked, "User", user.Id,
                AuditDetails.Json(("username", user.Username),
                    ("lockedUntil", localAttempt.LockedUntil?.ToString("o") ?? "unknown")), ct);
            ApiMetrics.AuthLoginAttempts.Add(1,
                new KeyValuePair<string, object?>("result", "failure"),
                new KeyValuePair<string, object?>("reason", "locked"));
            return Unauthorized(new { message = "Invalid credentials" });
        }

        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            var auditReason = localAttempt.TriggeredLockout
                ? "invalid_password_locked_out"
                : "invalid_password";
            await _audit.LogAsync(AuditActions.LoginFailed, "User", user.Id,
                AuditDetails.Json(("username", user.Username), ("reason", auditReason),
                    ("failedCount", localAttempt.FailureCount)), ct);
            ApiMetrics.AuthLoginAttempts.Add(1,
                new KeyValuePair<string, object?>("result", "failure"),
                new KeyValuePair<string, object?>("reason", "bad_password"));
            if (localAttempt.TriggeredLockout) ApiMetrics.AuthLockouts.Add(1);
            return Unauthorized(new { message = "Invalid credentials" });
        }

        // Disabled users are soft-deactivated; they keep their history but cannot get new tokens.
        // L-2 (security audit 2026-05-15): generic "Invalid credentials" response so an attacker
        // with a username list cannot distinguish disabled valid accounts from invalid creds.
        // The audit row + metric still carry the precise reason ("account_disabled") so an
        // operator inspecting the SIEM/audit trail sees the truth.
        if (!user.IsActive)
        {
            await ReleaseUserAttemptAsync(user, localAttempt, ct);
            await _audit.LogAsync(AuditActions.LoginFailed, "User", user.Id,
                AuditDetails.Json(("username", user.Username), ("reason", "account_disabled")), ct);
            ApiMetrics.AuthLoginAttempts.Add(1,
                new KeyValuePair<string, object?>("result", "failure"),
                new KeyValuePair<string, object?>("reason", "disabled"));
            return Unauthorized(new { message = "Invalid credentials" });
        }

        await ResetUserAttemptsAsync(user, ct);

        // PR0b: token-mint + cookie-set + LOGIN_SUCCESS audit are all centralised in
        // IAuthSessionIssuer so LDAP / Windows-Auth flows can reuse them. AuthSource.Local
        // for the BCrypt path; LDAP/Windows pass their own value.
        var session = await _sessionIssuer.IssueAsync(user, NodePilot.Api.Security.AuthSource.Local, HttpContext, ct);
        ApiMetrics.AuthLoginAttempts.Add(1,
            new KeyValuePair<string, object?>("result", "success"),
            new KeyValuePair<string, object?>("reason", "ok"));
        return SessionResult(session.Token, user, TokenInBodyRequested());
    }

    private async Task<BootstrapAdminCreation> TryCreateBootstrapAdminAsync(
        string username,
        string password,
        string presentedToken,
        IHostEnvironment environment,
        CancellationToken ct)
    {
        await using var localGate = await AdminAccountMutationGate.EnterLocalAsync(ct);
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // A retry or a wait behind another node must never reuse entities observed before
            // the invariant lock. All security-relevant state is loaded again below.
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, ct);
            await AdminAccountMutationGate.AcquireTransactionLockAsync(_db, ct);

            if (await _db.Users.AnyAsync(ct))
            {
                await transaction.RollbackAsync(ct);
                return BootstrapAdminCreation.UsersAlreadyExist();
            }

            // Re-read the owner-only token while the cross-node lock is held. A request that
            // passed the optimistic check cannot consume a token deleted or replaced while
            // it waited for another bootstrap attempt to finish.
            if (!NodePilot.Api.Security.AdminBootstrap.Validate(
                    environment, presentedToken, _config))
            {
                await transaction.RollbackAsync(ct);
                return BootstrapAdminCreation.TokenInvalid();
            }

            var created = new User
            {
                Id = Guid.NewGuid(),
                Username = username,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, BCryptWorkFactor),
                Role = UserRole.Admin,
                Provider = AuthProvider.Local,
                IsBreakGlass = true,
                PasswordChangedAt = DateTime.UtcNow,
            };
            _db.Users.Add(created);
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return BootstrapAdminCreation.Created(created);
        });
    }

    /// <summary>
    /// LDAP-first preamble for <see cref="Login"/>. Returns a non-null
    /// <see cref="ActionResult"/> when the LDAP path has produced a verdict that should be
    /// returned directly (success-token, hard 401 on refused collision or LDAP-only user with
    /// invalid creds). Returns <c>null</c> when the caller should fall through to the local
    /// password path — that's the behaviour for: LDAP feature not wired in, LDAP disabled
    /// in config, LDAP unavailable (infra failure / circuit open), or the user is a local
    /// account that LDAP doesn't know about.
    /// </summary>
    private async Task<ActionResult<LoginResponse>?> TryLdapLoginAsync(LoginRequest request, CancellationToken ct)
    {
        if (_ldapAuthenticator is null || _externalUserMapper is null || _ldapOptions is null)
            return null;
        var opts = _activeDirectoryAuthentication?.Ldap ?? _ldapOptions.CurrentValue;
        if (!_activeAuthentication.LdapEnabled || !opts.Enabled) return null;

        string canonicalUpn;
        try
        {
            canonicalUpn = UsernameNormalizer.ToUpn(request.Username, opts.UpnSuffix);
        }
        catch (ArgumentException)
        {
            await _audit.LogAsync(AuditActions.LoginFailed, "User", null,
                AuditDetails.Json(
                    ("username", SafeUsernameForAudit(request.Username)),
                    ("source", AuthSourceLdap),
                    ("reason", "invalid_directory_username")), ct);
            return Unauthorized(new { message = "Invalid credentials" });
        }

        // Look up an existing local row for this username — by raw username and by the
        // UPN form so an LDAP user typed as "alice" still matches their stored
        // "alice@firma.de" row. Used to decide:
        //   - whether to attempt LDAP at all (skip when the user is provably Local).
        //   - whether to fall back to local on InvalidCredentials (local-row exists).
        var existing = await _db.Users.FirstOrDefaultAsync(u => u.Username == request.Username, ct);
        if (existing is null)
        {
            if (!string.Equals(canonicalUpn, request.Username, StringComparison.OrdinalIgnoreCase))
                existing = await _db.Users.FirstOrDefaultAsync(u => u.Username == canonicalUpn, ct);
        }

        // Local-only users skip LDAP entirely. Without this branch a local "admin" account
        // with a unique-to-NodePilot username would be probed against AD on every login,
        // wasting a roundtrip and pointlessly logging "InvalidCredentials" against the DC.
        // A passwordless local collision deliberately reaches the mapper so the refusal is
        // audited. It is never promoted or merged into the external identity.
        var hasLocalRow = existing is not null && existing.Provider == AuthProvider.Local;
        if (hasLocalRow && !string.IsNullOrEmpty(existing!.PasswordHash)) return null;

        // M-2 (security audit 2026-05-15): per-account lockout for the LDAP path, mirroring the
        // local password path's H-4 policy. Previously the LDAP login had only the service-wide
        // LdapCircuitBreaker, which trips on infrastructure failures — NOT on credential
        // rejections — so an attacker could brute-force a single known account indefinitely. A
        // locked account is now refused before we even reach the directory; the generic message
        // does not reveal that the account exists. Scoped to provisioned LDAP rows — local
        // accounts are ratcheted by the local password path, unknown usernames by the
        // ExternalLoginThrottle above.
        if (existing is { Provider: AuthProvider.Ldap } && existing.LockedUntil is { } ldapLockedUntil && ldapLockedUntil > DateTime.UtcNow)
        {
            await _audit.LogAsync(AuditActions.LoginLocked, "User", existing.Id,
                AuditDetails.Json(("username", existing.Username),
                    ("lockedUntil", ldapLockedUntil.ToString("o")), ("source", AuthSourceLdap)), ct);
            ApiMetrics.AuthLoginAttempts.Add(1,
                new KeyValuePair<string, object?>("result", "failure"),
                new KeyValuePair<string, object?>("reason", "locked"));
            return Unauthorized(new { message = "Invalid credentials" });
        }

        // Claim a database-backed attempt slot before contacting AD. The unique slot key is
        // shared by every API node, so a parallel burst can no longer pass separate in-memory
        // IsBlocked checks on multiple nodes. Reservation itself is non-cancellable so a
        // client disconnect cannot interrupt after the database write but before we receive
        // the reservation token needed by the finally block.
        var externalAttempt = await _externalLoginThrottle.TryReserveAsync(
            canonicalUpn, DateTime.UtcNow, CancellationToken.None);
        if (!externalAttempt.IsAllowed)
        {
            await _audit.LogAsync(AuditActions.LoginLocked, "User", existing?.Id,
                AuditDetails.Json(
                    ("username", SafeUsernameForAudit(canonicalUpn)),
                    ("source", AuthSourceLdap),
                    ("reason", "pre_jit_account_throttle")), ct);
            return Unauthorized(new { message = "Invalid credentials" });
        }

        UserLoginReservation? ldapUserAttempt = null;
        var credentialVerdictReached = false;
        try
        {
            if (existing is { Provider: AuthProvider.Ldap })
            {
                ldapUserAttempt = await TryReserveUserAttemptAsync(
                    existing, DateTime.UtcNow, CancellationToken.None);
                if (!ldapUserAttempt.IsAllowed)
                {
                    await _audit.LogAsync(AuditActions.LoginLocked, "User", existing.Id,
                        AuditDetails.Json(("username", existing.Username),
                            ("lockedUntil", ldapUserAttempt.LockedUntil?.ToString("o") ?? "unknown"),
                            ("source", AuthSourceLdap)), ct);
                    return Unauthorized(new { message = "Invalid credentials" });
                }
            }

            var ldap = await _ldapAuthenticator.AuthenticateAsync(request.Username, request.Password, ct);
            credentialVerdictReached = ldap.Outcome is
                LdapAuthOutcome.Success or LdapAuthOutcome.InvalidCredentials;
            switch (ldap.Outcome)
            {
            case LdapAuthOutcome.Success:
            {
                // Credential success is authoritative even if the client disconnects while
                // mapping/session creation continues. Reset the failure state independently
                // of RequestAborted so a cancelled response cannot leave the account blocked.
                await _externalLoginThrottle.RecordSuccessAsync(
                    canonicalUpn, CancellationToken.None);
                if (existing is { Provider: AuthProvider.Ldap } && ldapUserAttempt is not null)
                    await ResetUserAttemptsAsync(existing, CancellationToken.None);
                var mapping = await _externalUserMapper.MapAsync(ldap.Result!, ct);
                if (mapping.Result == ExternalUserMapResult.RefusedUsernameCollision)
                {
                    // Audit was already written by the mapper; surface a generic 401 so the
                    // outsider can't tell collision from wrong-password.
                    ApiMetrics.AuthLoginAttempts.Add(1,
                        new KeyValuePair<string, object?>("result", "failure"),
                        new KeyValuePair<string, object?>("reason", "ldap_username_collision"));
                    return Unauthorized(new { message = "Invalid credentials" });
                }
                if (mapping.Result is ExternalUserMapResult.RefusedIdentityConflict
                    or ExternalUserMapResult.RefusedTombstoned
                    or ExternalUserMapResult.RefusedDirectoryAccess)
                {
                    ApiMetrics.AuthLoginAttempts.Add(1,
                        new KeyValuePair<string, object?>("result", "failure"),
                        new KeyValuePair<string, object?>("reason", "ldap_identity_refused"));
                    return Unauthorized(new { message = "Invalid credentials" });
                }
                if (mapping.Result == ExternalUserMapResult.RefusedBootstrapNotAdmin)
                {
                    // External identities never bootstrap the recovery administrator. The
                    // one-shot local bootstrap must establish a break-glass account first.
                    ApiMetrics.AuthLoginAttempts.Add(1,
                        new KeyValuePair<string, object?>("result", "failure"),
                        new KeyValuePair<string, object?>("reason", "ldap_bootstrap_refused"));
                    return Unauthorized(new
                    {
                        message = "Admin bootstrap required: bootstrap a local break-glass Admin first using the X-Setup-Token header.",
                    });
                }
                if (mapping.Result == ExternalUserMapResult.RefusedLastActiveAdmin)
                {
                    // The mapper preserved the database Admin invariant, invalidated stale
                    // sessions, and wrote the refusal audit. Do not mint a new token from
                    // that deliberately-preserved role.
                    ApiMetrics.AuthLoginAttempts.Add(1,
                        new KeyValuePair<string, object?>("result", "failure"),
                        new KeyValuePair<string, object?>("reason", "ldap_last_admin_demotion_refused"));
                    return Unauthorized(new { message = "Invalid credentials" });
                }

                var user = mapping.User!;
                if (!user.IsActive)
                {
                    await _audit.LogAsync(AuditActions.LoginFailed, "User", user.Id,
                        AuditDetails.Json(("username", user.Username), ("reason", "account_disabled"), ("source", AuthSourceLdap)), ct);
                    ApiMetrics.AuthLoginAttempts.Add(1,
                        new KeyValuePair<string, object?>("result", "failure"),
                        new KeyValuePair<string, object?>("reason", "disabled"));
                    return Unauthorized(new { message = "Invalid credentials" });
                }

                // A JIT-created user had no row to reserve above. Keep the persisted fields
                // clean and preserve the same success-reset contract as existing users.
                if (user.FailedLoginCount != 0 || user.LockedUntil is not null)
                    await ResetUserAttemptsAsync(user, ct);

                var session = await _sessionIssuer.IssueAsync(user, NodePilot.Api.Security.AuthSource.Ldap, HttpContext, ct);
                ApiMetrics.AuthLoginAttempts.Add(1,
                    new KeyValuePair<string, object?>("result", "success"),
                    new KeyValuePair<string, object?>("reason", "ldap_ok"));
                return SessionResult(session.Token, user, TokenInBodyRequested());
            }
            case LdapAuthOutcome.InvalidCredentials:
                // The directory cleanly rejected the credentials. If a local row exists we'd
                // have skipped LDAP entirely; reaching this branch means the user is either
                // unknown or known-as-LDAP. Either way: 401, no fall-through. Falling through
                // here would let an attacker bypass an AD password rejection by hammering a
                // local account that happens to share the username — the username-squat
                // attack that the identity-collision guard also defends against.
                //
                // M-2: ratchet the per-account failure counter for provisioned LDAP rows so
                // repeated LDAP rejections lock the account, matching the local path's
                // 10-strikes / 15-min policy. Unknown usernames have no row to track, and
                // Provider=Local collisions are excluded because no external account exists to
                // lock yet; the per-IP limiter remains the guard for unknown identities.
                var ldapTriggeredLockout = ldapUserAttempt?.TriggeredLockout == true;
                await _audit.LogAsync(
                    ldapTriggeredLockout ? AuditActions.LoginLocked : AuditActions.LoginFailed,
                    "User", existing?.Id,
                    AuditDetails.Json(("username", SafeUsernameForAudit(request.Username)),
                                      ("reason", "ldap_invalid_credentials"),
                                      ("failedCount", (ldapUserAttempt?.FailureCount ?? 0).ToString()),
                                      ("lockoutTriggered", ldapTriggeredLockout.ToString()),
                                      ("source", AuthSourceLdap)), ct);
                ApiMetrics.AuthLoginAttempts.Add(1,
                    new KeyValuePair<string, object?>("result", "failure"),
                    new KeyValuePair<string, object?>("reason", "ldap_invalid_credentials"));
                return Unauthorized(new { message = "Invalid credentials" });
            default:
                // Local password users were already short-circuited before LDAP. Reaching
                // this branch means an external login cannot establish authoritative
                // all-DC authorization state; fail with 503 rather than turning ambiguity
                // into either a local-password fallback or a misleading credential denial.
                ApiMetrics.AuthLoginAttempts.Add(1,
                    new KeyValuePair<string, object?>("result", "failure"),
                    new KeyValuePair<string, object?>("reason", ldap.UnavailableReason ?? "unavailable"));
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    message = "Directory authentication cannot verify current authorization.",
                });
            }
        }
        finally
        {
            if (!credentialVerdictReached)
            {
                // A cancelled request, unexpected adapter exception, blocked local row, or
                // directory outage produced no password verdict. Such paths must not consume
                // either the shared HA slot or the persisted per-user failure counter.
                try
                {
                    await _externalLoginThrottle.ReleaseAsync(
                        externalAttempt, CancellationToken.None);
                }
                finally
                {
                    if (existing is { Provider: AuthProvider.Ldap }
                        && ldapUserAttempt is not null)
                    {
                        await ReleaseUserAttemptAsync(
                            existing, ldapUserAttempt, CancellationToken.None);
                    }
                }
            }
        }
    }

    private const string AuthSourceLdap = "Ldap";

    /// <summary>
    /// PR5 — Windows Integrated Authentication (Negotiate / Kerberos). The endpoint is gated
    /// behind the <c>WindowsChallenge</c> auth scheme so only callers that completed a
    /// successful Negotiate handshake reach this code; everything else falls through to a
    /// 401 issued by the auth middleware. We then map the OS-side WindowsIdentity to a
    /// NodePilot User row via the same <see cref="ExternalUserMapper"/> the LDAP path uses
    /// and mint a session with <see cref="NodePilot.Api.Security.AuthSource.Windows"/>.
    /// <para>
    /// Disabled by default — only registered with the auth pipeline when
    /// <c>Authentication:Windows:Enabled</c> is true. Operators that don't want SSO simply
    /// leave the flag off and the endpoint returns 404 / "scheme not registered".
    /// </para>
    /// </summary>
    [HttpPost("windows")]
    [Microsoft.AspNetCore.Authorization.Authorize(AuthenticationSchemes = NodePilot.Api.Hosting.AuthenticationSetup.WindowsAuthSchemeName)]
    // A1 (security audit 2026-06-24): share the per-IP "login" limiter (50/min) with the local
    // and LDAP login paths. The Negotiate handshake itself is the primary gate, but an
    // unthrottled endpoint still lets a client flood malformed Negotiate attempts (each one
    // burns an auth-handler roundtrip); the limiter caps that abuse the same way it caps /login.
    [EnableRateLimiting("login")]
    public async Task<ActionResult<LoginResponse>> WindowsLogin(CancellationToken ct)
    {
        if (!_activeAuthentication.WindowsEnabled)
            return NotFound();
        if (_externalUserMapper is null)
            return StatusCode(503, new { message = "Windows authentication is not configured." });

        // [Authorize] above guarantees a non-null Identity. The Negotiate middleware emits
        // ClaimTypes.PrimarySid for the user + ClaimTypes.GroupSid * for transitive groups.
        // We read from claims rather than casting to WindowsIdentity so the endpoint stays
        // unit-testable on non-Windows runtimes.
        var principal = HttpContext.User;

        // Reject any principal explicitly identified as NTLM. Host/domain policy is still
        // required because the Negotiate stack cannot reliably expose the selected package
        // on every platform and hosting path.
        var authMechanism = principal.Identity?.AuthenticationType ?? string.Empty;
        var windowsPolicy = _activeDirectoryAuthentication?.Windows
            ?? _windowsOptions?.CurrentValue ?? new WindowsAuthOptions
        {
            Enabled = true,
            NtlmDisabledByPolicy = true,
        };
        if (!string.IsNullOrEmpty(authMechanism)
            && authMechanism.Equals("NTLM", StringComparison.OrdinalIgnoreCase))
        {
            await _audit.LogAsync(AuditActions.LoginFailed, "User", null,
                AuditDetails.Json(
                    ("reason", "windows_ntlm_disabled"),
                    ("source", "Windows"),
                    ("mechanism", authMechanism)), ct);
            ApiMetrics.AuthLoginAttempts.Add(1,
                new KeyValuePair<string, object?>("result", "failure"),
                new KeyValuePair<string, object?>("reason", "windows_ntlm_disabled"));
            return Unauthorized(new
            {
                message = "Kerberos required — NTLM fallback is disabled. Verify your client has a Kerberos ticket and the SPN is registered."
            });
        }
        if (!windowsPolicy.NtlmDisabledByPolicy)
        {
            await _audit.LogAsync(AuditActions.LoginFailed, "User", null,
                AuditDetails.Json(
                    ("reason", "windows_ntlm_policy_unverified"),
                    ("source", "Windows")), ct);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "Kerberos-only Windows authentication requires NTLM to be disabled by host/domain policy."
            });
        }
        var sidClaim = principal.FindFirstValue(ClaimTypes.PrimarySid);
        string? sid = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(sidClaim))
                sid = new SecurityIdentifier(sidClaim).ToString();
        }
        catch (ArgumentException)
        {
            // Fail below with the same generic missing/invalid identity response.
        }
        if (string.IsNullOrEmpty(sid))
        {
            await _audit.LogAsync(AuditActions.LoginFailed, "User", null,
                AuditDetails.Json(("reason", "windows_no_primary_sid"), ("source", "Windows")), ct);
            return Unauthorized(new { message = "Windows identity carries no PrimarySid claim — Negotiate misconfiguration." });
        }

        if (_directoryAdapter is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "Authoritative Windows directory lookup is not configured." });

        LdapDirectorySnapshot? snapshot;
        try
        {
            // Kerberos PAC group data can be hours old. A service-bind LDAPS lookup is the
            // authoritative admission/freshness source for both first JIT and every login.
            snapshot = await _directoryAdapter.LookupBySubjectAsync(sid, ct);
        }
        catch (LdapInfrastructureException ex)
        {
            await _audit.LogAsync(AuditActions.LoginFailed, "User", null,
                AuditDetails.Json(
                    ("reason", "windows_directory_unavailable"),
                    ("source", "Windows"),
                    ("errorClass", ex.GetType().Name)), ct);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "Windows authentication cannot verify current directory authorization." });
        }
        if (snapshot is null || !snapshot.IsEnabled
            || !string.Equals(snapshot.Subject, sid, StringComparison.OrdinalIgnoreCase))
        {
            await _audit.LogAsync(AuditActions.LoginFailed, "User", null,
                AuditDetails.Json(("reason", "windows_directory_account_disabled_or_missing"), ("source", "Windows")), ct);
            return Unauthorized(new { message = "Windows authentication refused." });
        }

        var ldapResult = new LdapAuthResult(
            ExternalId: sid,
            Upn: snapshot.Upn,
            DisplayName: snapshot.DisplayName,
            GroupSids: snapshot.GroupSids);
        var mapping = await _externalUserMapper.MapAsync(ldapResult, AuthProvider.Windows, ct);

        if (mapping.Result == ExternalUserMapResult.RefusedUsernameCollision)
        {
            ApiMetrics.AuthLoginAttempts.Add(1,
                new KeyValuePair<string, object?>("result", "failure"),
                new KeyValuePair<string, object?>("reason", "windows_username_collision"));
            return Unauthorized(new { message = "Windows authentication refused — username collision." });
        }
        if (mapping.Result is ExternalUserMapResult.RefusedIdentityConflict
            or ExternalUserMapResult.RefusedTombstoned
            or ExternalUserMapResult.RefusedDirectoryAccess)
        {
            ApiMetrics.AuthLoginAttempts.Add(1,
                new KeyValuePair<string, object?>("result", "failure"),
                new KeyValuePair<string, object?>("reason", "windows_identity_refused"));
            return Unauthorized(new { message = "Windows authentication refused." });
        }
        if (mapping.Result == ExternalUserMapResult.RefusedBootstrapNotAdmin)
        {
            ApiMetrics.AuthLoginAttempts.Add(1,
                new KeyValuePair<string, object?>("result", "failure"),
                new KeyValuePair<string, object?>("reason", "windows_bootstrap_refused"));
            return Unauthorized(new
            {
                message = "Admin bootstrap required: bootstrap a local break-glass Admin first using the X-Setup-Token header.",
            });
        }
        if (mapping.Result == ExternalUserMapResult.RefusedLastActiveAdmin)
        {
            ApiMetrics.AuthLoginAttempts.Add(1,
                new KeyValuePair<string, object?>("result", "failure"),
                new KeyValuePair<string, object?>("reason", "windows_last_admin_demotion_refused"));
            return Unauthorized(new
            {
                message = "Windows authentication refused because the directory mapping would remove the last active Admin.",
            });
        }

        var user = mapping.User!;
        if (!user.IsActive)
        {
            await _audit.LogAsync(AuditActions.LoginFailed, "User", user.Id,
                AuditDetails.Json(("username", user.Username), ("reason", "account_disabled"), ("source", "Windows")), ct);
            ApiMetrics.AuthLoginAttempts.Add(1,
                new KeyValuePair<string, object?>("result", "failure"),
                new KeyValuePair<string, object?>("reason", "disabled"));
            return Unauthorized(new { message = "Account is disabled" });
        }

        if (user.FailedLoginCount != 0 || user.LockedUntil is not null)
        {
            user.FailedLoginCount = 0;
            user.LockedUntil = null;
            await _db.SaveChangesAsync(ct);
        }

        var session = await _sessionIssuer.IssueAsync(user, NodePilot.Api.Security.AuthSource.Windows, HttpContext, ct);
        ApiMetrics.AuthLoginAttempts.Add(1,
            new KeyValuePair<string, object?>("result", "success"),
            new KeyValuePair<string, object?>("reason", "windows_ok"));
        // Windows SSO is browser-only and is driven by ambient OS credentials via the Negotiate
        // handshake — i.e. an XSS could trigger it without knowing any secret. So, unlike the
        // password-gated login paths, the token is NEVER returned in the body here: always
        // identity-only, the JWT stays in the httpOnly cookie.
        return SessionResult(session.Token, user, includeToken: false);
    }

    /// <summary>
    /// Revokes the caller's current token by writing its jti to the revocation list. The
    /// client should discard the token after calling this — future requests presenting it
    /// will be rejected by the revocation-check middleware until the token's natural expiry.
    /// </summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        // L-1b (security audit 2026-05-15): Logout must be reachable even when the caller
        // presents a revoked or expired cookie. The TokenValidityMiddleware short-circuits
        // any authenticated-but-revoked request with 401 BEFORE the handler runs unless the
        // endpoint is marked [AllowAnonymous]. Without that marker, "logout" on a stale
        // cookie would 401 instead of clearing the cookie, stranding the browser.
        //
        // H-5: always clear the browser cookies — even if the token is missing/malformed we
        // still want to scrub any stale cookie state so a broken session recovers cleanly.
        ClearAuthCookies();

        // TokenValidityMiddleware deliberately anonymizes stale/revoked cookies on this
        // [AllowAnonymous] recovery endpoint. It retains the signature-validated claims in
        // HttpContext.Items solely so logout can still revoke the corresponding DB session.
        var logoutPrincipal = User.Identity?.IsAuthenticated == true
            ? User
            : HttpContext.Items.TryGetValue(TokenValidityMiddleware.InvalidatedPrincipalItem, out var raw)
              && raw is ClaimsPrincipal invalidated
                ? invalidated
                : User;
        var jti = logoutPrincipal.FindFirstValue(JwtRegisteredClaimNames.Jti);
        var sessionIdClaim = logoutPrincipal.FindFirstValue(AuthSessionIssuer.SessionIdClaim);
        var userIdStr = logoutPrincipal.FindFirstValue(ClaimTypes.NameIdentifier);
        var expClaim = logoutPrincipal.FindFirstValue("exp");

        if (!Guid.TryParse(userIdStr, out var userId))
            return NoContent(); // nothing to revoke

        if (Guid.TryParse(sessionIdClaim, out var sessionId))
        {
            var authSession = await _db.AuthSessions.FindAsync([sessionId], ct);
            if (authSession is { RevokedAt: null } && authSession.UserId == userId)
                authSession.RevokedAt = DateTime.UtcNow;
        }

        if (string.IsNullOrEmpty(jti))
        {
            await _db.SaveChangesAsync(ct);
            return NoContent();
        }

        long expSec = 0;
        long.TryParse(expClaim, out expSec);
        var expiresAt = expSec > 0
            ? DateTimeOffset.FromUnixTimeSeconds(expSec).UtcDateTime
            : DateTime.UtcNow.Add(TokenLifetime);

        // Idempotent: if the jti is already revoked we leave it as is.
        var existing = await _db.RevokedTokens.FindAsync([jti], ct);
        if (existing is null)
        {
            _db.RevokedTokens.Add(new RevokedToken
            {
                Jti = jti,
                UserId = userId,
                RevokedAt = DateTime.UtcNow,
                ExpiresAt = expiresAt,
                Reason = "user-logout",
            });
            ApiMetrics.AuthTokenRevocations.Add(1,
                new KeyValuePair<string, object?>("reason", "user-logout"));
        }
        // Persist the session-family revocation even when this JTI was already present.
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditActions.Logout, "User", userId, null, ct);
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult> GetCurrentUser(CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var user = await _db.Users.FindAsync([Guid.Parse(userId)], ct);
        if (user is null) return Unauthorized();

        // Id is exposed so the SPA can compare against `Workflow.CheckedOutByUserId` to
        // decide "is this lock mine?". The cookie-based JWT is httpOnly and cannot be
        // decoded client-side, so /auth/me is the only path the SPA has to learn the id.
        return Ok(new { Id = user.Id, user.Username, Role = user.Role.ToString() });
    }

    /// <summary>
    /// Issues a fresh JWT to the caller based on their current (valid) bearer token AND
    /// revokes the presented token in the same transaction (rotation on refresh). Without
    /// rotation, a stolen token could be renewed indefinitely just by hitting this endpoint
    /// every 11 h — with rotation, the attacker and the legitimate client cannot both
    /// survive the next refresh: whoever calls second is rejected and has to re-login.
    ///
    /// Concurrency: the SPA must serialize refresh calls. Two simultaneous refreshes on the
    /// same token race; the loser gets 401 and its user gets bounced to the login screen,
    /// which is the intended "something is off, start fresh" behavior.
    ///
    /// The user row is re-fetched so a deleted or role-changed account cannot keep renewing.
    /// </summary>
    [HttpPost("refresh")]
    [Authorize]
    [EnableRateLimiting("refresh")]
    public async Task<ActionResult<LoginResponse>> Refresh(CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null || !Guid.TryParse(userId, out var id))
            return Unauthorized();

        var user = await _db.Users.FindAsync([id], ct);
        if (user is null) return Unauthorized(new { message = "User no longer exists" });

        // Mint the new token first so a failure below (e.g. DB unavailable) leaves the old
        // token valid — we prefer "client keeps using current token" over "user gets locked
        // out mid-session". Routed through IAuthSessionIssuer.RefreshAsync so the rotated
        // JWT carries the same baseline claims as the original login. Group authorization
        // is deliberately not token-borne (server-side DirectoryMemberships), so a refresh
        // can never gain or lose group-folder permissions.
        IssuedSession session;
        try
        {
            session = await _sessionIssuer.RefreshAsync(user, HttpContext, ct);
        }
        catch (UnauthorizedAccessException)
        {
            ClearAuthCookies();
            return Unauthorized(new { message = "Session is no longer active" });
        }
        var newToken = session.Token;

        // Revoke the presented token. If the caller racingly refreshes twice the second
        // request will already find the jti revoked and return 401 from the middleware;
        // that's the intended behavior.
        var presentedJti = User.FindFirstValue(JwtRegisteredClaimNames.Jti);
        var expClaim = User.FindFirstValue("exp");
        if (!string.IsNullOrEmpty(presentedJti))
        {
            long expSec = 0; long.TryParse(expClaim, out expSec);
            var expiresAt = expSec > 0
                ? DateTimeOffset.FromUnixTimeSeconds(expSec).UtcDateTime
                : DateTime.UtcNow.Add(TokenLifetime);

            var existing = await _db.RevokedTokens.FindAsync([presentedJti], ct);
            if (existing is null)
            {
                _db.RevokedTokens.Add(new RevokedToken
                {
                    Jti = presentedJti,
                    UserId = id,
                    RevokedAt = DateTime.UtcNow,
                    ExpiresAt = expiresAt,
                    Reason = "rotated",
                });
                await _db.SaveChangesAsync(ct);
                ApiMetrics.AuthTokenRevocations.Add(1,
                    new KeyValuePair<string, object?>("reason", "rotated"));
            }
        }

        // Cookies were already rotated by RefreshAsync above (np_auth + np_csrf both set
        // on this response). The H-5 invariant — XSS that stole the old CSRF value loses
        // it on every successful refresh — still holds because RefreshAsync emits a fresh
        // 256-bit CSRF token per call.

        // Audit the refresh so a stolen-token-being-renewed scenario leaves a forensic
        // trail. Distinct from LOGIN_SUCCESS to avoid double-counting active sessions in
        // SIEM dashboards (12h token lifetime × N sessions would otherwise dwarf real
        // login signal). MapEventCategory maps TOKEN_REFRESHED to event.category=iam so
        // it groups with the rest of the auth events.
        await _audit.LogAsync(AuditActions.TokenRefreshed, "User", id,
            AuditDetails.Json(("username", user.Username), ("role", user.Role.ToString())), ct);

        // Return the rotated JWT in the body ONLY to a real Bearer caller (CLI / API). A
        // cookie-authenticated browser — or an XSS riding the browser's np_auth cookie — gets
        // identity-only; the rotated token reaches it solely through the refreshed httpOnly
        // cookie. This is the H-5 invariant: no browser-reachable endpoint hands out the token.
        return SessionResult(newToken, user, AuthenticatedViaBearerHeader());
    }

    private enum BootstrapAdminCreationStatus
    {
        Created,
        UsersAlreadyExist,
        TokenInvalid,
    }

    private sealed record BootstrapAdminCreation(
        BootstrapAdminCreationStatus Status,
        User? User)
    {
        public static BootstrapAdminCreation Created(User user)
            => new(BootstrapAdminCreationStatus.Created, user);

        public static BootstrapAdminCreation UsersAlreadyExist()
            => new(BootstrapAdminCreationStatus.UsersAlreadyExist, null);

        public static BootstrapAdminCreation TokenInvalid()
            => new(BootstrapAdminCreationStatus.TokenInvalid, null);
    }

    private sealed record UserLoginReservation(
        bool IsAllowed,
        int FailureCount,
        bool TriggeredLockout,
        DateTime? LockedUntil,
        DateTime? AppliedBlockedUntil)
    {
        public static UserLoginReservation Allowed(
            int failureCount,
            bool triggeredLockout,
            DateTime? appliedBlockedUntil)
            => new(true, failureCount, triggeredLockout, appliedBlockedUntil, appliedBlockedUntil);

        public static UserLoginReservation Blocked(DateTime? lockedUntil)
            => new(false, 0, false, lockedUntil, null);
    }
}
