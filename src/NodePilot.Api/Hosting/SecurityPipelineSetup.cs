namespace NodePilot.Api.Hosting;

/// <summary>
/// Production-only pipeline hardening: global exception handler that hides stack traces +
/// SQL errors behind a ProblemDetails wrapper, plus the standard set of security response
/// headers (HSTS, CSP, X-Frame-Options, …). No-op in Development so the dev-loop keeps
/// returning rich error pages.
/// </summary>
public static class SecurityPipelineSetup
{
    public static WebApplication UseNodePilotSecurityHeaders(this WebApplication app)
    {
        if (app.Environment.IsDevelopment()) return app;

        app.UseExceptionHandler();
        app.UseHsts();
        // Security headers. Keep CSP permissive enough for the SPA — it inlines styles and
        // connects to the same-origin SignalR hub. Tighten once an asset pipeline with
        // hashing/nonces is in place.
        app.Use(async (ctx, next) =>
        {
            var headers = ctx.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
            if (!headers.ContainsKey("Content-Security-Policy"))
            {
                // Tightened CSP (audit L4 + M-3):
                //  - object-src 'none'        → blocks <object>/<embed>/<applet> plugin loaders
                //  - form-action 'self'       → forms can only POST back to our own origin
                //  - frame-src 'none'         → no nested browsing contexts at all
                //  - ws:/wss: dropped         → 'self' already covers SignalR's same-origin upgrades
                //  - style-src split (M-3)    → 'unsafe-inline' moved from generic style-src to
                //                               style-src-attr only, so XSS-injected <style> tags
                //                               are blocked while inline style="..." attributes
                //                               (used by React Flow transforms / Tailwind runtime
                //                               utilities) keep working. style-src-elem 'self'
                //                               is the strict <style>/<link> policy. CSP3 in all
                //                               supported browsers (Chromium 75+ / FF 86+ / Safari 14+).
                headers["Content-Security-Policy"] =
                    "default-src 'self'; " +
                    "img-src 'self' data:; " +
                    "style-src 'self'; " +
                    "style-src-elem 'self'; " +
                    "style-src-attr 'unsafe-inline'; " +
                    "script-src 'self'; " +
                    "connect-src 'self'; " +
                    "object-src 'none'; " +
                    "form-action 'self'; " +
                    "frame-src 'none'; " +
                    "frame-ancestors 'none'; " +
                    "base-uri 'self'";
            }
            await next();
        });
        return app;
    }
}
