using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NodePilot.Api.Security;
using NodePilot.Api.Security.Ldap;
using NodePilot.Api.Security.Oidc;
using NodePilot.Api.Security.Scim;

namespace NodePilot.Api.Hosting;

/// <summary>
/// JWT-Bearer + cookie-fallback wiring for the API. The browser SPA authenticates via the
/// httpOnly <c>np_auth</c> cookie (audit H-5/H-6 — the JWT never reaches JS-readable storage),
/// while server-to-server callers and integration tests keep using the <c>Authorization: Bearer</c>
/// header. Both paths converge on the same JwtBearer middleware via the OnMessageReceived hook.
/// </summary>
public static class AuthenticationSetup
{
    public static IServiceCollection AddNodePilotAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        // Resolve exactly once. JwtBearer validation and AuthSessionIssuer must observe the
        // same immutable key even if the backing file changes after host setup.
        var jwtKeyProvider = new JwtKeyProvider(configuration, environment);
        var jwtKey = jwtKeyProvider.Key;
        services.AddSingleton(ActiveAuthenticationConfiguration.From(configuration));
        services.AddSingleton(ActiveDirectoryAuthenticationConfiguration.From(configuration));

        // Audit M-2: cached JWT-key provider so AuthController does not re-validate on every
        // token mint. Validation still runs once here at startup — misconfig fails loud.
        services.AddSingleton<IJwtKeyProvider>(jwtKeyProvider);

        // PR0b (LDAP groundwork): centralised session-issuer used by every auth path —
        // local BCrypt, LDAP-bind, and Windows-Negotiate. Same JWT shape, same cookie
        // flags, same audit envelope, regardless of how the user authenticated.
        services.AddScoped<NodePilot.Api.Security.IAuthSessionIssuer,
            NodePilot.Api.Security.AuthSessionIssuer>();

        // PR0c: Bind LDAP / Windows-Auth options. Both default to Enabled=false, so an
        // operator who never touches Authentication:* keeps the legacy local-only flow.
        services.Configure<LdapOptions>(configuration.GetSection(LdapOptions.SectionName));
        services.Configure<WindowsAuthOptions>(configuration.GetSection(WindowsAuthOptions.SectionName));
        services.Configure<AuthenticationPolicyOptions>(
            configuration.GetSection(AuthenticationPolicyOptions.SectionName));
        services.Configure<EnterpriseOidcOptions>(
            configuration.GetSection(EnterpriseOidcOptions.SectionName));
        services.Configure<ScimOptions>(configuration.GetSection(ScimOptions.SectionName));

        // Authentication settings are restart-bound. Freeze the option values now so an
        // Admin Settings write cannot change mapper/evaluator/SCIM behaviour before the
        // OIDC handler and schemes have been rebuilt on restart. The live IConfiguration
        // remains available to the Settings UI as the pending configuration.
        services.AddSingleton<IOptions<AuthenticationPolicyOptions>>(
            Options.Create(configuration.GetSection(AuthenticationPolicyOptions.SectionName)
                .Get<AuthenticationPolicyOptions>() ?? new AuthenticationPolicyOptions()));
        services.AddSingleton<IOptions<EnterpriseOidcOptions>>(
            Options.Create(configuration.GetSection(EnterpriseOidcOptions.SectionName)
                .Get<EnterpriseOidcOptions>() ?? new EnterpriseOidcOptions()));
        services.AddSingleton<IOptions<ScimOptions>>(
            Options.Create(configuration.GetSection(ScimOptions.SectionName)
                .Get<ScimOptions>() ?? new ScimOptions()));
        services.AddSingleton<ScimAuthentication>();
        services.AddSingleton<OidcTicketStore>();
        services.AddScoped<OidcIdentityMapper>();
        services.AddScoped<ExternalAuthorizationEvaluator>();
        services.AddScoped<ScimProvisioningService>();

        // PR2: LDAP bind path. The circuit breaker is a singleton — one shared state across
        // all login attempts, so a DC outage trips the breaker for the whole instance, not
        // per-request. The adapter does its own short-lived LdapConnection per call so it
        // can be scoped (or singleton — it has no per-request state). The authenticator is
        // scoped because it pulls IOptionsMonitor + ILogger via DI in the normal way.
        services.AddSingleton<LdapCircuitBreaker>();
        // DB-backed and scoped: attempt reservations are shared through IdempotencyKeys, so
        // concurrent requests and HA nodes contend on the same unique slots. The scoped
        // lifetime lets the throttle share the request DbContext without singleton->scoped
        // lifetime violations.
        services.AddScoped<ExternalLoginThrottle>();
        services.AddSingleton<ILdapConnectionAdapter, SystemLdapConnectionAdapter>();
        services.AddScoped<LdapAuthenticator>();

        // PR3: just-in-time (JIT) mapper for LDAP / Windows users — creates/updates the local
        // User record the first time such a user logs in, plus group-aware authorization.
        // Scoped so the mapper can pull the per-request DbContext + the writer that
        // AuthController already uses for LOGIN_* audit entries.
        services.AddScoped<ExternalUserMapper>();

        // Audit H-17: TokenValidityMiddleware caches revocation + user-state checks in a 30s
        // TTL MemoryCache to stop a burst of authenticated requests from hammering the DB.
        services.AddMemoryCache();

        var authBuilder = services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = configuration["Jwt:Issuer"] ?? "NodePilot",
                    ValidAudience = configuration["Jwt:Audience"] ?? "NodePilot",
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
                    // Tighten default 5-minute skew — all our services run on the same host.
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
                // Audit H-5 / H-6: when no Authorization header is present (= non-Bearer client),
                // fall back to the np_auth cookie. Bearer callers keep working unchanged. The
                // SignalR query-string ?access_token= workaround is removed — with cookies the
                // browser attaches credentials to the WebSocket upgrade automatically, so the
                // JWT no longer ends up in reverse-proxy access logs / Referer headers.
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        if (string.IsNullOrEmpty(ctx.Request.Headers.Authorization)
                            && ctx.Request.Cookies.TryGetValue(
                                NodePilot.Api.Controllers.AuthController.AuthCookieName,
                                out var cookieToken)
                            && !string.IsNullOrEmpty(cookieToken))
                        {
                            ctx.Token = cookieToken;
                        }
                        return Task.CompletedTask;
                    }
                };
            });

        // PR5: optionally register the Negotiate scheme for the /api/auth/windows endpoint.
        // We register the scheme under the explicit name "WindowsChallenge" rather than the
        // Negotiate default, so the scheme is only used by endpoints that opt into it via
        // [Authorize(AuthenticationSchemes = "WindowsChallenge")]. The default scheme stays
        // JwtBearer, so every existing endpoint continues to expect a JWT cookie / header.
        var windowsAuthSection = configuration.GetSection(WindowsAuthOptions.SectionName);
        var windowsAuthEnabled = windowsAuthSection.GetValue<bool>(nameof(WindowsAuthOptions.Enabled));
        if (windowsAuthEnabled)
        {
            // Negotiate handler accepts both Kerberos and NTLM by default; the Microsoft
            // implementation does NOT expose a "Kerberos-only" wire-level switch. Operators
            // who don't want NTLM either disable it at the OS / domain policy level
            // (recommended) or rely on the application-level check in
            // AuthController.WindowsLogin which rejects Identity.AuthenticationType == "NTLM"
            // when AllowNtlmFallback=false.
            authBuilder.AddNegotiate(WindowsAuthSchemeName, _ => { });
        }

        var oidcSection = configuration.GetSection(EnterpriseOidcOptions.SectionName);
        if (oidcSection.GetValue<bool>(nameof(EnterpriseOidcOptions.Enabled)))
        {
            var authority = oidcSection[nameof(EnterpriseOidcOptions.Authority)]!;
            var clientId = oidcSection[nameof(EnterpriseOidcOptions.ClientId)]!;
            var clientSecret = oidcSection[nameof(EnterpriseOidcOptions.ClientSecret)]!;
            var nameClaimType = oidcSection[nameof(EnterpriseOidcOptions.NameClaimType)]
                ?? "preferred_username";
            var configuredScopes = oidcSection
                .GetSection(nameof(EnterpriseOidcOptions.Scopes)).Get<string[]>()
                ?? ["openid", "profile", "email"];

            authBuilder
                .AddCookie(OidcExternalSchemeName, options =>
                {
                    options.Cookie.Name = "np_oidc_tmp";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
                    options.SlidingExpiration = false;
                })
                .AddOpenIdConnect(OidcChallengeSchemeName, options =>
                {
                    options.Authority = authority;
                    options.ClientId = clientId;
                    options.ClientSecret = clientSecret;
                    options.SignInScheme = OidcExternalSchemeName;
                    options.ResponseType = "code";
                    options.UsePkce = true;
                    options.RequireHttpsMetadata = true;
                    options.GetClaimsFromUserInfoEndpoint = true;
                    options.MapInboundClaims = false;
                    options.TokenValidationParameters.ValidateIssuer = true;
                    options.TokenValidationParameters.ValidateAudience = true;
                    options.TokenValidationParameters.NameClaimType = nameClaimType;
                    options.Scope.Clear();
                    foreach (var scope in configuredScopes
                                 .Append("openid")
                                 .Where(s => !string.IsNullOrWhiteSpace(s))
                                 .Distinct(StringComparer.Ordinal))
                    {
                        options.Scope.Add(scope);
                    }
                    options.Events = new OpenIdConnectEvents
                    {
                        OnRemoteFailure = context =>
                        {
                            context.HandleResponse();
                            context.Response.Redirect("/login?oidcError=authentication_failed");
                            return Task.CompletedTask;
                        },
                    };
                });
            services.AddOptions<CookieAuthenticationOptions>(OidcExternalSchemeName)
                .Configure<OidcTicketStore>((options, ticketStore) =>
                    options.SessionStore = ticketStore);
        }

        services.AddAuthorization();
        return services;
    }

    /// <summary>
    /// Auth-scheme name reserved for the Negotiate / Kerberos challenge. Only used by the
    /// explicit <c>POST /api/auth/windows</c> endpoint; every other endpoint stays on the
    /// JwtBearer default.
    /// </summary>
    public const string WindowsAuthSchemeName = "WindowsChallenge";
    public const string OidcExternalSchemeName = "OidcExternal";
    public const string OidcChallengeSchemeName = "OidcChallenge";
}
