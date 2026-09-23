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
/// Every documentation address is its own prerendered index.html, so each page carries its own
/// title and description. The bundle is built with a relative Vite base, so a page's assets
/// resolve against the document url: at /docs/de/cli/ they become /docs/de/cli/../../assets/...,
/// which is why each file states its own depth and why an address without a trailing slash
/// redirects rather than being served. The redirect is behaviour under test, not an
/// implementation detail.
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
            // One prerendered page, the shape the docs build writes for every address.
            var page = Path.Combine(docsRoot, "de", "cli");
            Directory.CreateDirectory(page);
            File.WriteAllText(Path.Combine(page, "index.html"), "<html>docs page cli</html>");
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
        // The catch-all over /docs carries the same `nonfile` constraint as the SPA fallback, so
        // an asset request never selects it. Without that, a broken asset reference would reach
        // a browser as a confusing MIME error instead of a plain 404.
        var response = await GetAsync("/docs/assets/missing.js");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DocsPage_ServesItsOwnFile_NotTheDocsRootAndNotTheAppShell()
    {
        // The root shell would look like a working page and render nothing: its assets are
        // relative to the documentation root and would be looked for two directories too deep.
        var response = await GetAsync("/docs/de/cli/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        (await response.Content.ReadAsStringAsync()).Should().Contain("docs page cli");
    }

    [Fact]
    public async Task DocsPageWithoutTrailingSlash_RedirectsToTheDirectory()
    {
        var response = await GetAsync("/docs/de/cli");

        response.StatusCode.Should().Be(HttpStatusCode.MovedPermanently);
        response.Headers.Location!.ToString().Should().Be("/docs/de/cli/");
        response.Headers.Location!.IsAbsoluteUri.Should().BeFalse();
    }

    [Fact]
    public async Task UnknownDocsPage_Is404_AndNeitherShell()
    {
        var response = await GetAsync("/docs/de/nope/");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("docs site");
        body.Should().NotContain("app spa");
    }

    [Theory]
    [InlineData("../index.html")]
    [InlineData("de/../../index.html")]
    [InlineData("..")]
    public void PageResolution_RefusesAnAddressThatLeavesTheDocumentationDirectory(string rest)
    {
        // Routing normalises a ".." away before the endpoint is even selected, so this is tested
        // where it lives rather than over HTTP, where it can never be reached.
        var docsRoot = Path.Combine(_contentRoot, "wwwroot", "docs");

        DocsSiteSetup.TryResolveDocsPage(docsRoot, rest, out _).Should().BeFalse();
    }

    [Fact]
    public void PageResolution_AcceptsAPrerenderedPage()
    {
        var docsRoot = Path.Combine(_contentRoot, "wwwroot", "docs");
        Directory.CreateDirectory(Path.Combine(docsRoot, "de", "cli"));
        File.WriteAllText(Path.Combine(docsRoot, "de", "cli", "index.html"), "<html>docs page cli</html>");

        DocsSiteSetup.TryResolveDocsPage(docsRoot, "de/cli", out var page).Should().BeTrue();
        page.Should().Be(Path.Combine(docsRoot, "de", "cli", "index.html"));
    }

    [Fact]
    public async Task WithoutADocsBundle_ADeepAddressStaysWithTheSpaFallback()
    {
        var response = await GetAsync("/docs/de/cli/", withDocsBundle: false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("app spa");
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
