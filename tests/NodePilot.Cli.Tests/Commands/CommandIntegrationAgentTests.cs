using FluentAssertions;
using NodePilot.Cli.Tests.Infra;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Cli.Tests.Commands;

[Collection(CommandTestCollection.Name)]
public sealed class CommandIntegrationAgentTests
{
    [Fact]
    public void AgentEvents_PassesCursorAndSerializesMemberEvidence()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/agents/runs/{id}/events").WithParam("after", "7").WithParam("pageSize", "20").UsingGet())
            .RespondWith(Response.Create().WithBodyAsJson(new[] { new { agentRunId = id, sequence = 8, kind = "tool_completed", memberId = "researcher", content = "evidence" } }));
        var result = h.Run("agent", "events", id.ToString(), "--after", "7", "--page-size", "20");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("researcher").And.Contain("evidence");
    }

    [Fact]
    public void SkillEnabled_HandlesNoContent_WithoutAttemptingJsonDeserialization()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/agents/skills/{id}/enabled").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(204));
        h.Run("agent", "skill", "enabled", id.ToString(), "false").ExitCode.Should().Be(ExitCodes.Success);
    }
}
