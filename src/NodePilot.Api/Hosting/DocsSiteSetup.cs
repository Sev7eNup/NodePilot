namespace NodePilot.Api.Hosting;

/// <summary>
/// Serves the documentation SPA (src/nodepilot-docs-ui) from wwwroot/docs at /docs — the same
/// bundle that goes to GitHub Pages, shipped with the product so an operator on a disconnected
/// network has the runbooks next to the installation, matching the installed version.
/// </summary>
public static class DocsSiteSetup
{
    /// <summary>Directory under the web root that the build drops the docs bundle into.</summary>
    public const string DocsDirectoryName = "docs";

    /// <summary>Request path the docs bundle is served under.</summary>
    public const string DocsRequestPath = "/" + DocsDirectoryName;

    /// <summary>
    /// Maps the two entry points of the docs bundle. Everything else it needs — the hashed
    /// assets and fonts — is already served by UseStaticFiles from the same web root.
    ///
    /// These have to be endpoints rather than a UseDefaultFiles/UseStaticFiles pair: the SPA
    /// catch-all (MapFallbackToFile) matches any extension-less path, so routing selects an
    /// endpoint for /docs and /docs/ long before those middlewares run, and both deliberately
    /// step aside once an endpoint is present. Asset requests carry a file extension, fail the
    /// catch-all's `nonfile` constraint, and therefore still reach the static-file middleware —
    /// which is why a missing asset stays a real 404 instead of returning the app shell.
    ///
    /// Call after the routing/endpoint section is set up and before the SPA fallback.
    /// </summary>
    public static WebApplication MapNodePilotDocsSite(this WebApplication app)
    {
        // WebRootPath is null when no wwwroot was staged; the source tree and the test host both
        // run that way. Read it after Build(), because UseWindowsService() re-roots the app.
        var webRoot = app.Environment.WebRootPath;
        if (string.IsNullOrEmpty(webRoot)) return app;

        var indexPath = Path.Combine(webRoot, DocsDirectoryName, "index.html");
        if (!File.Exists(indexPath)) return app;

        // One endpoint, because route matching ignores a trailing slash: "/docs" and "/docs/"
        // are the same template and mapping both is an ambiguous match. The raw request path
        // still carries the difference, and it matters — the bundle is built with Vite
        // `base: './'`, so index.html references its assets relatively and they resolve against
        // the DOCUMENT url. Served at /docs/ that yields /docs/assets/..., served at /docs it
        // would yield /assets/... and hit the main SPA's bundle instead: a blank page.
        //
        // The redirect Location stays relative on purpose. It needs no scheme or host, so a
        // proxied https request can never be sent onward to http:// — which an absolute Location
        // built from the server's own scheme could do.
        //
        // Anonymous by design: the runbooks have to be readable when signing in is the problem.
        // The content is the public documentation site, so this discloses nothing.
        app.MapGet(DocsRequestPath, (HttpContext http) =>
                http.Request.Path.Value!.EndsWith('/')
                    ? Results.File(indexPath, "text/html")
                    : Results.Redirect($"{DocsRequestPath}/", permanent: true))
            .AllowAnonymous();

        return app;
    }
}
