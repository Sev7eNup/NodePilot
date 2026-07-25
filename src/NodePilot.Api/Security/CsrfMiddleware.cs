using Microsoft.AspNetCore.Http;
using NodePilot.Api.Controllers;

namespace NodePilot.Api.Security;

/// <summary>
/// Double-submit CSRF guard (audit H-5 companion). When a request authenticates via the
/// <c>np_auth</c> httpOnly cookie, the browser attaches it automatically to any same-site
/// POST — which would let a malicious cross-origin form submission use the victim's session.
/// The guard requires a matching <c>X-CSRF-Token</c> header whose value equals the
/// JS-readable <c>np_csrf</c> cookie that only same-origin code can read under SameSite=Strict.
///
/// Non-browser clients that use the <c>Authorization: Bearer ...</c> header are skipped: the
/// browser never auto-attaches Authorization headers, so CSRF does not apply to them.
/// Safe methods (GET/HEAD/OPTIONS) are also skipped — they must not mutate state.
///
/// Pipeline order: register AFTER <c>UseAuthentication</c> is fine but not required. Must
/// run BEFORE the controller action handler. The middleware does not depend on an
/// authenticated <c>HttpContext.User</c>; it only looks at cookies + headers.
/// </summary>
public sealed class CsrfMiddleware
{
    private readonly RequestDelegate _next;

    public CsrfMiddleware(RequestDelegate next) { _next = next; }

    public async Task InvokeAsync(HttpContext context)
    {
        if (ShouldEnforce(context)
            && (!context.Request.Cookies.TryGetValue(AuthController.CsrfCookieName, out var csrfCookie)
                || !context.Request.Headers.TryGetValue("X-CSRF-Token", out var csrfHeaderRaw)
                || string.IsNullOrEmpty(csrfCookie)
                || !SecretComparer.FixedTimeEquals(csrfHeaderRaw.ToString(), csrfCookie)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"error\":\"CSRF token missing or invalid\",\"code\":\"csrf_mismatch\"}");
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// Returns true when the request must carry a CSRF token:
    /// <list type="bullet">
    ///   <item>Mutating method (POST/PUT/PATCH/DELETE)</item>
    ///   <item>Caller authenticates via the <c>np_auth</c> cookie (not Bearer header)</item>
    ///   <item>Not targeting a cookie-bootstrap endpoint (login has no cookie yet)</item>
    /// </list>
    /// External webhook / trigger endpoints use their own secrets (X-Api-Key, X-Webhook-Secret)
    /// and are never browser-authenticated, so the cookie-presence check already excludes them.
    /// </summary>
    private static bool ShouldEnforce(HttpContext ctx)
    {
        var method = ctx.Request.Method;
        if (HttpMethods.IsGet(method)
            || HttpMethods.IsHead(method)
            || HttpMethods.IsOptions(method)
            || HttpMethods.IsTrace(method))
            return false;

        // Bearer requests are not browser-originated; browsers do not auto-attach Authorization.
        // Restrict the exemption to the Bearer scheme explicitly. A blanket "Authorization
        // header present → skip CSRF" let an attacker bypass the cookie-gated CSRF check by
        // tacking on a header of an unsupported scheme (e.g. Basic, Negotiate) — the header is
        // ignored by JwtBearer but the request still carries the np_auth cookie. The CSRF check
        // must still fire in that scenario; only valid Bearer Authorization headers signal a
        // non-browser API client.
        if (ctx.Request.Headers.TryGetValue("Authorization", out var authValue))
        {
            var raw = authValue.ToString();
            if (raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // No cookie → either unauthenticated (downstream will 401) or a non-cookie scheme.
        if (!ctx.Request.Cookies.ContainsKey(AuthController.AuthCookieName)) return false;

        // Login itself has no pre-existing cookie in the happy path; if one is replayed from
        // an earlier session and the password is valid, we WILL set a fresh cookie regardless
        // of whether CSRF was provided, so gating login on CSRF is pointless. Login already
        // has its own rate limit + bootstrap-token + password check.
        //
        // /api/auth/windows is the same kind of cookie-bootstrap endpoint: it is Negotiate-
        // gated (needs a Kerberos ticket the browser only presents in the Intranet zone) and
        // mints a fresh np_auth + np_csrf pair on success. Without this exemption a re-triggered
        // SSO while a *stale* np_auth cookie without a matching np_csrf is still present would
        // 403 before the handshake. It shares login's rate limiter, so it is not left unguarded.
        var path = ctx.Request.Path;
        if (path.StartsWithSegments("/api/auth/login")
            || path.StartsWithSegments("/api/auth/windows")) return false;

        return true;
    }
}
