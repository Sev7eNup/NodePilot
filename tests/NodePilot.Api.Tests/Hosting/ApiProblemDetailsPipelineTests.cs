using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Tests.TestSupport;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

[Collection(ApiPipelineCollection.Name)] // serialize full-host boots — see ApiPipelineCollection
public sealed class ApiProblemDetailsPipelineTests
{
    [Fact]
    public async Task ProgramPipeline_NormalizesLegacyControllerErrorPayloadsToProblemDetails()
    {
        using var factory = new ApiPipelineFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/api/trigger/missing-workflow",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(StatusCodes.Status401Unauthorized);
        problem.Title.Should().Be("Unauthorized");
        problem.Detail.Should().Be("Invalid or missing X-Api-Key header");
        problem.Extensions["code"].Should().BeOfType<JsonElement>()
            .Which.GetString().Should().Be("UNAUTHORIZED");
    }
}
