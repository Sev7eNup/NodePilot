using NodePilot.Ai;
using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.Interfaces;
using Xunit;

namespace NodePilot.Ai.Tests;

public class WorkflowChatToolRegistryTests
{
    private static readonly WorkflowChatToolRegistry Registry = new();

    private static ChatToolContext Ctx(string json = """{"nodes":[],"edges":[]}""",
        IExecutionLogReader? logs = null, Guid? workflowId = null)
    {
        using var doc = JsonDocument.Parse(json);
        return new ChatToolContext(doc.RootElement.Clone(), workflowId ?? Guid.NewGuid(), logs);
    }

    [Fact]
    public void GetTools_ExposesReadonlyToolDefinitions()
    {
        var names = Registry.GetTools(Ctx()).Select(t => t.Name).ToHashSet();
        names.Should().Contain("analyze_workflow");
        names.Should().Contain("list_activity_types");
    }

    [Fact]
    public async Task ExecuteAsync_AnalyzeWorkflow_ReturnsFindingsJson()
    {
        var ctx = Ctx("""
            {"nodes":[
              {"id":"t1","type":"activity","data":{"activityType":"scheduleTrigger","config":{}}},
              {"id":"lonely","type":"activity","data":{"activityType":"log","config":{}}}
            ],"edges":[]}
            """);
        var result = await Registry.ExecuteAsync("analyze_workflow", "{}", ctx, CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        result.Should().Contain("orphan-node");
    }

    [Fact]
    public async Task ExecuteAsync_ListActivityTypes_IncludesKnownTypes()
    {
        var result = await Registry.ExecuteAsync("list_activity_types", "", Ctx(), CancellationToken.None);
        result.Should().Contain("scheduleTrigger");
        result.Should().Contain("runScript");
    }

    [Fact]
    public async Task ExecuteAsync_UnknownTool_ReturnsErrorObjectNotThrow()
    {
        var result = await Registry.ExecuteAsync("delete_everything", "{}", Ctx(), CancellationToken.None);
        using var doc = JsonDocument.Parse(result);
        doc.RootElement.TryGetProperty("error", out _).Should().BeTrue();
    }

    // ---- Execution-Log-Tools: Advertising ------------------------------------------

    [Fact]
    public void GetTools_WithExecutionLogReader_IncludesExecutionTools()
    {
        var names = Registry.GetTools(Ctx(logs: new FakeExecutionLogReader())).Select(t => t.Name).ToHashSet();
        names.Should().Contain("list_recent_executions");
        names.Should().Contain("get_execution_steps");
        names.Should().Contain("get_failure_context");
    }

    [Fact]
    public void GetTools_WithoutExecutionLogReader_OmitsExecutionTools()
    {
        var names = Registry.GetTools(Ctx()).Select(t => t.Name).ToHashSet();
        names.Should().Contain("analyze_workflow");
        names.Should().Contain("list_activity_types");
        names.Should().NotContain("list_recent_executions");
        names.Should().NotContain("get_execution_steps");
        names.Should().NotContain("get_failure_context");
    }

    [Fact]
    public async Task ExecuteAsync_ExecutionTool_WithoutReader_ReturnsErrorJson()
    {
        // Not advertised doesn't mean not callable — the model can hallucinate tool names.
        var result = await Registry.ExecuteAsync("list_recent_executions", "{}", Ctx(), CancellationToken.None);
        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("error").GetString().Should().Contain("nicht verfügbar");
    }

    // ---- list_recent_executions -----------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ListRecentExecutions_ReturnsRunsWithFailedStepNames()
    {
        var fake = new FakeExecutionLogReader();
        var execId = Guid.NewGuid();
        fake.Executions.Add(FakeExecutionLogReader.Summary(execId, "Failed", errorMessage: "Step X kaputt",
            stepsTotal: 3, failedSteps: [new FailedStepInfo("step-2", null), new FailedStepInfo("step-3", "Cleanup")]));
        var wfId = Guid.NewGuid();

        var result = await Registry.ExecuteAsync("list_recent_executions", "{}", Ctx(logs: fake, workflowId: wfId), CancellationToken.None);

        fake.LastWorkflowId.Should().Be(wfId);
        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
        var run = doc.RootElement.GetProperty("executions")[0];
        run.GetProperty("id").GetGuid().Should().Be(execId);
        run.GetProperty("status").GetString().Should().Be("Failed");
        run.GetProperty("errorMessage").GetString().Should().Be("Step X kaputt");
        run.GetProperty("stepsTotal").GetInt32().Should().Be(3);
        run.GetProperty("durationMs").GetInt64().Should().Be(5000);
        var failed = run.GetProperty("failedSteps");
        failed.GetArrayLength().Should().Be(2);
        failed[0].GetProperty("stepId").GetString().Should().Be("step-2"); // unlabeled step → falls back to a stable step ID
        failed[0].GetProperty("stepName").ValueKind.Should().Be(JsonValueKind.Null);
        failed[1].GetProperty("stepName").GetString().Should().Be("Cleanup");
    }

    [Fact]
    public async Task ExecuteAsync_ListRecentExecutions_ClampsTakeParameter()
    {
        var fake = new FakeExecutionLogReader();
        await Registry.ExecuteAsync("list_recent_executions", """{"take":999}""", Ctx(logs: fake), CancellationToken.None);
        fake.LastTake.Should().Be(20);

        await Registry.ExecuteAsync("list_recent_executions", """{"take":0}""", Ctx(logs: fake), CancellationToken.None);
        fake.LastTake.Should().Be(1);

        await Registry.ExecuteAsync("list_recent_executions", "{}", Ctx(logs: fake), CancellationToken.None);
        fake.LastTake.Should().Be(10); // default
    }

    [Fact]
    public async Task ExecuteAsync_ListRecentExecutions_TruncatesLongErrorMessage()
    {
        var fake = new FakeExecutionLogReader();
        fake.Executions.Add(FakeExecutionLogReader.Summary(Guid.NewGuid(), "Failed",
            errorMessage: new string('e', 600)));

        var result = await Registry.ExecuteAsync("list_recent_executions", "{}", Ctx(logs: fake), CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        var msg = doc.RootElement.GetProperty("executions")[0].GetProperty("errorMessage").GetString()!;
        msg.Should().StartWith(new string('e', 500));
        msg.Should().Contain("Zeichen abgeschnitten]");
        msg.Length.Should().BeLessThan(600); // 500-char cap + truncation marker, not the full 600
    }

    // ---- get_execution_steps --------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_GetExecutionSteps_InvalidGuid_ReturnsErrorJson()
    {
        var result = await Registry.ExecuteAsync("get_execution_steps", """{"executionId":"not-a-guid"}""",
            Ctx(logs: new FakeExecutionLogReader()), CancellationToken.None);
        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("error").GetString().Should().Contain("GUID");
    }

    [Fact]
    public async Task ExecuteAsync_GetExecutionSteps_UnknownExecution_ReturnsErrorJson()
    {
        var result = await Registry.ExecuteAsync("get_execution_steps",
            $$"""{"executionId":"{{Guid.NewGuid()}}"}""", Ctx(logs: new FakeExecutionLogReader()), CancellationToken.None);
        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("error").GetString().Should().Contain("nicht gefunden");
    }

    [Fact]
    public async Task ExecuteAsync_GetExecutionSteps_TruncatesLongOutput()
    {
        var fake = new FakeExecutionLogReader();
        var execId = Guid.NewGuid();
        fake.StepsByExecution[execId] = new ExecutionStepLogs(
            FakeExecutionLogReader.Summary(execId, "Failed"),
            [FakeExecutionLogReader.Step("s1", "Failed", output: new string('x', 10_000), errorOutput: "kurz")]);

        var result = await Registry.ExecuteAsync("get_execution_steps",
            $$"""{"executionId":"{{execId}}"}""", Ctx(logs: fake), CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        var step = doc.RootElement.GetProperty("steps")[0];
        var output = step.GetProperty("output").GetString()!;
        output.Should().Contain("Zeichen abgeschnitten]");
        output.Length.Should().BeLessThan(1_600); // 1500-char cap + truncation marker
        step.GetProperty("errorOutput").GetString().Should().Be("kurz");
        doc.RootElement.GetProperty("truncatedSteps").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_GetExecutionSteps_MoreThan100Steps_CapsListAndSetsTruncatedFlag()
    {
        var fake = new FakeExecutionLogReader();
        var execId = Guid.NewGuid();
        var steps = Enumerable.Range(0, 105)
            .Select(i => FakeExecutionLogReader.Step($"s{i}"))
            .ToList();
        fake.StepsByExecution[execId] = new ExecutionStepLogs(
            FakeExecutionLogReader.Summary(execId, "Failed", stepsTotal: steps.Count), steps);

        var result = await Registry.ExecuteAsync("get_execution_steps",
            $$"""{"executionId":"{{execId}}"}""", Ctx(logs: fake), CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("steps").GetArrayLength().Should().Be(100);
        doc.RootElement.GetProperty("truncatedSteps").GetBoolean().Should().BeTrue();
    }

    // ---- get_failure_context --------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_GetFailureContext_NoFailedRun_ReturnsMessage()
    {
        var fake = new FakeExecutionLogReader();
        fake.Executions.Add(FakeExecutionLogReader.Summary(Guid.NewGuid(), "Succeeded"));

        var result = await Registry.ExecuteAsync("get_failure_context", "{}", Ctx(logs: fake), CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("message").GetString().Should().Contain("Keine fehlgeschlagenen");
    }

    [Fact]
    public async Task ExecuteAsync_GetFailureContext_ReturnsFailingStepsOfLatestFailedRun()
    {
        var fake = new FakeExecutionLogReader();
        var okId = Guid.NewGuid();
        var failedId = Guid.NewGuid();
        fake.Executions.Add(FakeExecutionLogReader.Summary(okId, "Succeeded"));
        fake.Executions.Add(FakeExecutionLogReader.Summary(failedId, "Failed", errorMessage: "Boom"));
        fake.StepsByExecution[failedId] = new ExecutionStepLogs(
            FakeExecutionLogReader.Summary(failedId, "Failed", errorMessage: "Boom"),
            [
                FakeExecutionLogReader.Step("s1", "Succeeded", output: "ok"),
                FakeExecutionLogReader.Step("s2", "Failed", errorOutput: "Zugriff verweigert"),
            ]);

        var result = await Registry.ExecuteAsync("get_failure_context", "{}", Ctx(logs: fake), CancellationToken.None);

        fake.LastExecutionId.Should().Be(failedId); // the Failed run, not the Succeeded one before it
        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("execution").GetProperty("status").GetString().Should().Be("Failed");
        var failing = doc.RootElement.GetProperty("failingSteps");
        failing.GetArrayLength().Should().Be(1); // only Failed steps, not s1
        failing[0].GetProperty("stepId").GetString().Should().Be("s2");
        failing[0].GetProperty("errorOutput").GetString().Should().Be("Zugriff verweigert");
    }

    [Fact]
    public async Task ExecuteAsync_GetFailureContext_PicksNewestFailedRun_NotAnOlderOne()
    {
        // Reader contract: newest-first. With TWO Failed runs, the one listed first (most
        // recent) must be picked — otherwise the tool would return stale failure context.
        var fake = new FakeExecutionLogReader();
        var newerFailed = Guid.NewGuid();
        var olderFailed = Guid.NewGuid();
        fake.Executions.Add(FakeExecutionLogReader.Summary(Guid.NewGuid(), "Succeeded"));
        fake.Executions.Add(FakeExecutionLogReader.Summary(newerFailed, "Failed"));
        fake.Executions.Add(FakeExecutionLogReader.Summary(olderFailed, "Failed"));
        fake.StepsByExecution[newerFailed] = new ExecutionStepLogs(
            FakeExecutionLogReader.Summary(newerFailed, "Failed"),
            [FakeExecutionLogReader.Step("s1", "Failed", errorOutput: "neuester Fehler")]);
        fake.StepsByExecution[olderFailed] = new ExecutionStepLogs(
            FakeExecutionLogReader.Summary(olderFailed, "Failed"),
            [FakeExecutionLogReader.Step("s1", "Failed", errorOutput: "alter Fehler")]);

        var result = await Registry.ExecuteAsync("get_failure_context", "{}", Ctx(logs: fake), CancellationToken.None);

        fake.LastExecutionId.Should().Be(newerFailed);
        result.Should().Contain("neuester Fehler").And.NotContain("alter Fehler");
    }

    [Fact]
    public async Task ExecuteAsync_GetFailureContext_TruncatesAt2000NotAt1500()
    {
        var fake = new FakeExecutionLogReader();
        var failedId = Guid.NewGuid();
        fake.Executions.Add(FakeExecutionLogReader.Summary(failedId, "Failed"));
        fake.StepsByExecution[failedId] = new ExecutionStepLogs(
            FakeExecutionLogReader.Summary(failedId, "Failed"),
            [FakeExecutionLogReader.Step("s1", "Failed", errorOutput: new string('x', 5_000))]);

        var result = await Registry.ExecuteAsync("get_failure_context", "{}", Ctx(logs: fake), CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        var errorOutput = doc.RootElement.GetProperty("failingSteps")[0].GetProperty("errorOutput").GetString()!;
        errorOutput.Should().Contain("Zeichen abgeschnitten]");
        errorOutput.Length.Should().BeGreaterThan(1_600); // NOT the 1500-char get_execution_steps cap
        errorOutput.Length.Should().BeLessThan(2_100);    // the 2000-char failure-context cap + marker
    }
}
