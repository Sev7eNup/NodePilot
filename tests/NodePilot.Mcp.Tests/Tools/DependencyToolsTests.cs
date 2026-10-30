using FluentAssertions;
using NodePilot.Mcp.Tests.Infra;
using NodePilot.Mcp.Tools;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Mcp.Tests.Tools;

public class DependencyToolsTests
{
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
