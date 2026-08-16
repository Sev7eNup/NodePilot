using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Tests.TestSupport;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

/// <summary>
/// An unmatched <c>/api</c> path must answer 404 ProblemDetails, not the SPA bundle.
///
/// <para>The SPA fallback matches whatever no endpoint claimed, which used to include every
/// unmatched /api path: a typo, an endpoint that moved, or a route parameter that failed its
/// type constraint all returned <c>200 text/html</c> with index.html. Clients read that as
/// success — measured against a lab install, a GET on <c>/api/triggers</c> and on
/// <c>/api/global-variables/not-a-guid</c> both produced a 200 HTML page, and `np`, the MCP
/// server and the SPA's own error handling all treated it as a valid response body. A missing
/// endpoint is exactly the failure that must be loud.</para>
/// </summary>
[Collection(ApiPipelineCollection.Name)] // serialize full-host boots — see ApiPipelineCollection
public sealed class ApiNotFoundFallbackTests
{
    [Theory]
    // Nothing has ever been mounted here.
    [InlineData("/api/there-is-no-such-endpoint")]
    // A real controller prefix, but no action at that path — the shape that made this
    // surface in the first place.
    [InlineData("/api/workflows/00000000-0000-0000-0000-000000000001/not-an-action")]
    // Route parameter fails its :guid constraint, so no endpoint matches at all.
    [InlineData("/api/global-variables/not-a-guid")]
    public async Task UnmatchedApiPath_Returns404ProblemDetails_NotTheSpaBundle(string path)
    {
        using var factory = new ApiPipelineFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(StatusCodes.Status404NotFound);
        problem.Extensions["code"].Should().BeOfType<JsonElement>()
            .Which.GetString().Should().Be("NOT_FOUND");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("<!doctype", "the SPA bundle must never answer an API path");
    }

    /// <summary>
    /// The fallback is scoped to /api only. Deep links the SPA owns — /workflows/&lt;id&gt; and
    /// friends — must keep reaching index.html, otherwise a page refresh 404s.
    /// </summary>
    [Fact]
    public async Task NonApiDeepLink_StillReachesTheSpaFallback()
    {
        using var factory = new ApiPipelineFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/workflows/00000000-0000-0000-0000-000000000001");

        // The test host has no built SPA bundle, so index.html is absent and the fallback
        // 404s on the FILE. What matters is that it is not the API problem response: the
        // request still routed to the SPA fallback rather than being claimed by the /api one.
        response.Content.Headers.ContentType?.MediaType.Should().NotBe("application/problem+json");
    }

    /// <summary>A path that a real endpoint owns must not be shadowed by the fallback.</summary>
    [Fact]
    public async Task MatchedApiPath_IsUnaffected()
    {
        using var factory = new ApiPipelineFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/workflows");

        // Unauthenticated, so 401 — the point is that it is the endpoint answering, not the
        // fallback's 404.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
