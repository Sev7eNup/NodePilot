namespace NodePilot.Api.Security;

/// <summary>
/// Single source of truth for the auth + CSRF cookie flags. Both the set-path
/// (<see cref="AuthSessionIssuer"/>) and the clear-path (<see
/// cref="NodePilot.Api.Controllers.AuthController.ClearAuthCookies"/>)
/// route through here so the Secure / SameSite / Path triple is symmetric.
///
/// Matching options prevent a TLS-terminating reverse proxy from producing different set and
/// delete cookies when forwarded headers are incorrect.
/// </summary>
public static class AuthCookieOptionsBuilder
{
    /// <summary>
    /// True when the cookie must be marked Secure: always in non-Development
    /// environments (production-direct-HTTPS deployment topology), otherwise mirror
    /// the actual transport so <c>dotnet run --urls http://localhost:5000</c> still
    /// works in dev.
    /// </summary>
    public static bool ResolveSecure(HttpContext httpContext, IHostEnvironment? environment)
    {
        if (environment is not null && !environment.IsDevelopment())
            return true;
        return httpContext.Request.IsHttps;
    }

    public static CookieOptions ForAuth(HttpContext httpContext, IHostEnvironment? environment, DateTimeOffset expiresAt)
        => new()
        {
            HttpOnly = true,
            Secure = ResolveSecure(httpContext, environment),
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = expiresAt,
        };

    public static CookieOptions ForCsrf(HttpContext httpContext, IHostEnvironment? environment, DateTimeOffset expiresAt)
        => new()
        {
            HttpOnly = false,
            Secure = ResolveSecure(httpContext, environment),
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = expiresAt,
        };
}
