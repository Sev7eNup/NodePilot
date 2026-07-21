using System.Text.Json;
using FluentAssertions;
using NodePilot.Mcp.Tests.Infra;
using NodePilot.Mcp.Tools;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Mcp.Tests;

public sealed class ExecutionControlToolsTests
{
    private static object ExecJson(Guid id, Guid wf, string status) => new
    {
        id, workflowId = wf, status, startedAt = DateTime.UtcNow,
        completedAt = (DateTime?)null, triggeredBy = "manual", errorMessage = (string?)null,
    };

    [Fact]
    public async Task ExecuteWorkflow_Returns202ExecutionId()
    {
        var wf = Guid.NewGuid();
        var exec = Guid.NewGuid();
        using var api = new TestApi();
        api.Server
            .Given(Request.Create().WithPath($"/api/workflows/{wf}/execute").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(202).WithBodyAsJson(ExecJson(exec, wf, "Pending")));

        var tools = new ExecutionTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.ExecuteWorkflow(wf.ToString(),
            parameters: new Dictionary<string, string> { ["env"] = "prod" }));

        json.Should().Contain(exec.ToString());
        json.Should().Contain("Pending");
    }

    [Fact]
    public async Task TriggerExternal_PassesApiKeyAndReportsIdempotentReplay()
    {
        var wf = Guid.NewGuid();
        var exec = Guid.NewGuid();
        using var api = new TestApi();
        api.Server
            .Given(Request.Create().WithPath("/api/trigger/Nightly").UsingPost()
                .WithHeader("X-Api-Key", "secret-key"))
            .RespondWith(Response.Create().WithStatusCode(202)
                .WithHeader("Idempotent-Replayed", "true")
                .WithBodyAsJson(ExecJson(exec, wf, "Running")));

        var tools = new ExecutionTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.TriggerExternalWorkflow(
            "Nightly", "secret-key", idempotencyKey: "abc-123"));

        json.Should().Contain(exec.ToString());
        json.Should().Contain("\"idempotentReplayed\":true");
    }

    [Fact]
    public async Task ResumeExecution_RejectsBadMode()
    {
        using var api = new TestApi();
        var tools = new ExecutionTools(api.Client());
        var act = async () => await tools.ResumeExecution(Guid.NewGuid().ToString(), "step-1", "nonsense");
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task ResumeExecution_SuccessPath_SendsStepIdOnTheWireAndReturns204()
    {
        var exec = Guid.NewGuid();
        using var api = new TestApi();
        // The real API requires a non-empty stepId in the body and answers 204 on success.
        api.Server
            .Given(Request.Create().WithPath($"/api/executions/{exec}/resume").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));

        var tools = new ExecutionTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.ResumeExecution(exec.ToString(), "step-7", "continue"));

        json.Should().Contain("\"resumed\":true");

        // Regression guard: an earlier version of this tool dropped stepId before sending the
        // request — assert it actually reaches the wire.
        var body = api.Server.LogEntries.Last().RequestMessage.Body ?? "";
        body.Should().Contain("step-7");
        body.Should().Contain("continue");
    }

    [Fact]
    public async Task ListPausedSteps_ReturnsStepIds()
    {
        var exec = Guid.NewGuid();
        using var api = new TestApi();
        api.Server
            .Given(Request.Create().WithPath($"/api/executions/{exec}/paused-steps").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[] { "step-7", "step-9" }));

        var tools = new ExecutionTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.ListPausedSteps(exec.ToString()));
        json.Should().Contain("step-7");
        json.Should().Contain("step-9");
    }
}
