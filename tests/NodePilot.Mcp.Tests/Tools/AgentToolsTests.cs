using System.Text.Json;
using NodePilot.Mcp.Api.Dtos;
using NodePilot.Mcp.Tests.Infra;
using NodePilot.Mcp.Tools;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Mcp.Tests.Tools;

public sealed class AgentToolsTests
{
    [Fact]
    public async Task Journal_PassesPagingAndExecutionIdentityToAuthorizedApi()
    {
        using var api = new TestApi();
        var id = Guid.NewGuid();
        api.Server.Given(Request.Create().WithPath("/api/agents/runs").WithParam("executionId", id.ToString()).UsingGet())
            .RespondWith(Response.Create().WithBodyAsJson(new[] { new { id, workflowExecutionId = id, stepId = "team", status = "Succeeded" } }));
        api.Server.Given(Request.Create().WithPath($"/api/agents/runs/{id}/events").WithParam("after", "12").WithParam("pageSize", "50").UsingGet())
            .RespondWith(Response.Create().WithBodyAsJson(new[] { new { agentRunId = id, sequence = 13, kind = "member_completed", memberId = "researcher", content = "evidence" } }));
        var tools = new AgentTools(api.Client());
        Assert.Contains("Succeeded", JsonSerializer.Serialize(await tools.ListAgentRuns(id.ToString(), TestContext.Current.CancellationToken)));
        Assert.Contains("researcher", JsonSerializer.Serialize(await tools.GetAgentEvents(id.ToString(), 12, 50, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task SkillToggle_HandlesNoContent_AndDiscoveryUsesRegisteredId()
    {
        using var api = new TestApi();
        var id = Guid.NewGuid();
        api.Server.Given(Request.Create().WithPath($"/api/agents/skills/{id}/enabled").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(204));
        api.Server.Given(Request.Create().WithPath($"/api/agents/mcp-servers/{id}/tools").UsingGet())
            .RespondWith(Response.Create().WithBodyAsJson(new[] { new { name = "registered", schema = new { type = "object" } } }));
        var tools = new AgentTools(api.Client());
        Assert.Contains("true", JsonSerializer.Serialize(await tools.SetAgentSkillEnabled(id.ToString(), false, TestContext.Current.CancellationToken)));
        Assert.Contains("registered", JsonSerializer.Serialize(await tools.DiscoverAgentMcpTools(id.ToString(), TestContext.Current.CancellationToken)));
    }
}
