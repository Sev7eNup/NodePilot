using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using NodePilot.Api.Hosting;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

/// <summary>
/// Pins how the bundled documentation is served at /docs.
///
/// The bundle is built with a relative Vite base, so its assets resolve against the document
/// url: at /docs/ they become /docs/assets/..., at /docs they become /assets/... and hit the
/// main SPA's bundle instead, leaving a blank page. The trailing-slash redirect is therefore
/// behaviour under test, not an implementation detail.
/// </summary>
public sealed class DocsSiteSetupTests : IDisposable
{
    private readonly string _contentRoot =
        Path.Combine(Path.GetTempPath(), $"nodepilot-docs-site-{Guid.NewGuid():N}");

    /// <param name="withDocsBundle">
    /// False models an installation whose web root carries no docs bundle — a source tree or a
    /// test host. The extension has to stay inert there rather than claim the path.
    /// </param>
    private async Task<HttpResponseMessage> GetAsync(
        string path,
        bool withDocsBundle = true,
        Action<HttpRequestMessage>? configureRequest = null)
    {
        var webRoot = Path.Combine(_contentRoot, "wwwroot");
        Directory.CreateDirectory(webRoot);
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "<html>app spa</html>");

        if (withDocsBundle)
        {
            var docsRoot = Path.Combine(webRoot, "docs");
            Directory.CreateDirectory(Path.Combine(docsRoot, "assets"));
            File.WriteAllText(Path.Combine(docsRoot, "index.html"), "<html>docs site</html>");
            File.WriteAllText(Path.Combine(docsRoot, "assets", "app.js"), "export default 1;");
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
            ContentRootPath = _contentRoot,
        });
        builder.WebHost.UseTestServer();
        var app = builder.Build();

        // Mirrors Program.cs: forwarded headers, static files, then the docs endpoints ahead of
        // the SPA catch-all. The catch-all has to be present — it is what makes the ordering
        // matter, since it matches every extension-less path including /docs and /docs/.
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
        });
        app.UseStaticFiles();
        app.MapNodePilotDocsSite();
        app.MapFallbackToFile("index.html");

        await app.StartAsync();
        try
        {
            var client = app.GetTestClient();
            // Redirects are inspected, not followed: the 301 itself is the contract.
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            configureRequest?.Invoke(request);
            return await client.SendAsync(request);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task DocsWithoutTrailingSlash_RedirectsSoRelativeAssetsResolve()
    {
        var response = await GetAsync("/docs");

        response.StatusCode.Should().Be(HttpStatusCode.MovedPermanently);
        response.Headers.Location!.ToString().Should().Be("/docs/");
    }

    [Fact]
    public async Task DocsRoot_ServesTheDocumentationIndex_NotTheAppShell()
    {
        var response = await GetAsync("/docs/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        (await response.Content.ReadAsStringAsync()).Should().Contain("docs site");
    }

    [Fact]
    public async Task DocsAsset_IsServedWithItsOwnContentType()
    {
        var response = await GetAsync("/docs/assets/app.js");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/javascript");
    }

    [Fact]
    public async Task MissingDocsAsset_Is404_NotTheAppShell()
    {
        // A catch-all over /docs would answer 200 text/html here, and a broken asset reference
        // would reach a browser as a confusing MIME error instead of a plain 404.
        var response = await GetAsync("/docs/assets/missing.js");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Redirect_CannotDowngradeTheSchemeBehindATlsTerminatingProxy()
    {
        // A relative Location is resolved by the browser against the request url, so a proxied
        // https request can never be sent onward to http:// — the failure mode an absolute
        // Location built from the server's own scheme would have.
        var response = await GetAsync("/docs", configureRequest: request =>
        {
            request.Headers.Add("X-Forwarded-Proto", "https");
            request.Headers.Add("X-Forwarded-Host", "nodepilot.contoso.local");
        });

        var location = response.Headers.Location!;
        location.IsAbsoluteUri.Should().BeFalse();
        location.ToString().Should().Be("/docs/");
    }

    [Fact]
    public async Task ApplicationRoot_StillBelongsToTheSpaFallback()
    {
        var response = await GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("app spa");
    }

    [Fact]
    public async Task WithoutADocsBundle_TheExtensionStaysInert()
    {
        var response = await GetAsync("/docs", withDocsBundle: false);

        response.StatusCode.Should().NotBe(HttpStatusCode.MovedPermanently);
    }

    public void Dispose()
    {
        if (Directory.Exists(_contentRoot)) Directory.Delete(_contentRoot, recursive: true);
    }
}
