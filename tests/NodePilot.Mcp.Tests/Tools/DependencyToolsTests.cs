using FluentAssertions;
using NodePilot.Mcp.Tests.Infra;
using NodePilot.Mcp.Tools;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Mcp.Tests.Tools;

public class DependencyToolsTests
{
    [Theory]
    [InlineData("list")]
    [InlineData("report")]
    [InlineData("evidence")]
    public async Task DependencyRequest_InvalidTlsConfiguration_SendsNothing(string operation)
    {
        using var api = new TestApi();
        using var http = new HttpClient { BaseAddress = new Uri(api.Url) };
        var client = new NodePilot.Mcp.Api.NodePilotApiClient(http)
        {
            Session = new NodePilot.Mcp.Config.SessionContext(api.Url, "default", "synthetic", false,
                SkipTlsVerification: true, TlsConfigurationError: "Invalid certificate pin"),
        };
        Func<Task> action = operation switch
        {
            "list" => () => client.ListDependencyObjectsAsync(null, null, null, null, null, 10, TestContext.Current.CancellationToken),
            "report" => () => client.GetDependenciesAsync("workflow", Guid.NewGuid(), "uses", null, 10, TestContext.Current.CancellationToken),
            _ => () => client.GetDependencyEvidenceAsync("workflow", Guid.NewGuid(), "machine", Guid.NewGuid(), "uses", null, 10, TestContext.Current.CancellationToken),
        };

        await action.Should().ThrowAsync<NodePilot.Mcp.Api.NotConfiguredException>().WithMessage("*Invalid certificate pin*");
        api.Server.LogEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task ListObjects_PreservesSearchAndPagination()
    {
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/dependencies/objects").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { items = Array.Empty<object>(), analysisRevision = "rev", nextCursor = "next" }));
        var response = await new DependencyTools(api.Client()).ListObjects("A & B", "workflow", "unused", "usage", "cursor", 10, TestContext.Current.CancellationToken);
        response.NextCursor.Should().Be("next");
        var request = api.Server.LogEntries.Should().ContainSingle().Subject.RequestMessage;
        request.RawQuery.Should().Contain("q=A%20%26%20B").And.Contain("state=unused").And.Contain("sort=usage")
            .And.Contain("cursor=cursor").And.Contain("limit=10");
    }

    [Fact]
    public async Task GetEvidence_UsesSelectedPairAndDirection()
    {
        using var api = new TestApi(); var id = Guid.NewGuid(); var related = Guid.NewGuid();
        api.Server.Given(Request.Create().WithPath($"/api/dependencies/machine/{id}/evidence").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { analysisRevision = "rev", paths = Array.Empty<object>(), items = Array.Empty<object>(), total = 0, nextCursor = (string?)null }));
        var result = await new DependencyTools(api.Client()).GetEvidence("machine", id, "workflow", related, "usedBy", cancellationToken: TestContext.Current.CancellationToken);
        result.AnalysisRevision.Should().Be("rev");
        api.Server.LogEntries.Should().ContainSingle().Subject.RequestMessage.RawQuery.Should().Contain($"relatedId={related}").And.Contain("direction=usedBy");
    }
}
