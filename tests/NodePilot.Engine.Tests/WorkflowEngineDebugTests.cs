using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests;

/// <summary>
/// End-to-end integration tests for the step debugger. Pattern: start a workflow that has
/// a breakpointed step (as a fire-and-forget task), poll until the execution is paused,
/// trigger resume, then wait for completion. Verifies that the engine's pause/resume
/// machinery correctly interoperates with the external resume-command interface.
/// </summary>
[Collection("SerialEngineTests")]
public class WorkflowEngineDebugTests
{
    private readonly NodePilotDbContext _db;
    private readonly IServiceProvider _serviceProvider;
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly Mock<IActivityExecutor> _mockExecutor;
    private readonly Mock<IExecutionNotifier> _notifier;
    private Dictionary<string, string>? _capturedVariables;
    private readonly WorkflowEngine _engine;

    public WorkflowEngineDebugTests()
    {
        _mockExecutor = new Mock<IActivityExecutor>();
        _mockExecutor.Setup(e => e.ActivityType).Returns("runScript");
        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .Callback<StepExecutionContext, JsonElement, CancellationToken>((ctx, _, _) =>
            {
                // Snapshot the variables at execute time so we can verify that resume
                // overrides were actually merged in.
                _capturedVariables = new Dictionary<string, string>(ctx.Variables);
            })
            .ReturnsAsync(new ActivityResult { Success = true, Output = "OK" });

        // Plain manualTrigger root stub — MUST NOT share the runScript capture callback,
        // otherwise the trigger would clobber _capturedVariables before the breakpointed step runs.
        var manualTriggerExecutor = new Mock<IActivityExecutor>();
        manualTriggerExecutor.Setup(e => e.ActivityType).Returns("manualTrigger");
        manualTriggerExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult { Success = true, Output = "{}" });

        var registry = new ActivityRegistry(new[] { _mockExecutor.Object, manualTriggerExecutor.Object });
        (_db, var sp, _connection) = TestDbContext.CreateWithScopedServices(registry);
        _serviceProvider = sp;
        _notifier = new Mock<IExecutionNotifier>();
        _engine = new WorkflowEngine(_db, registry, NullLogger<WorkflowEngine>.Instance, _serviceProvider, _notifier.Object);
    }

    /// <summary>Builds a two-node workflow with a breakpoint on the second step. The engine
    /// runs step 1 immediately (no breakpoint), reaches step 2, and pauses there. The test
    /// can then inspect the paused state and trigger resume.</summary>
    private static string BuildBreakpointWorkflow(bool breakpointOnFirstStep = false, bool breakpointOnSecondStep = true)
    {
        var bp1 = breakpointOnFirstStep ? ",\"breakpoint\":true" : "";
        var bp2 = breakpointOnSecondStep ? ",\"breakpoint\":true" : "";
        return "{\"nodes\":[" +
               "{\"id\":\"trigger-1\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"activityType\":\"manualTrigger\",\"config\":{}}}," +
               "{\"id\":\"step-1\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"activityType\":\"runScript\",\"config\":{}" + bp1 + "}}," +
               "{\"id\":\"step-2\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"activityType\":\"runScript\",\"config\":{}" + bp2 + "}}" +
               "],\"edges\":[{\"id\":\"te\",\"source\":\"trigger-1\",\"target\":\"step-1\"},{\"id\":\"e1\",\"source\":\"step-1\",\"target\":\"step-2\"}]}";
    }

    private Workflow CreateWorkflow(string definitionJson) => new Workflow
    {
        Id = Guid.NewGuid(),
        Name = "TestWF",
        DefinitionJson = definitionJson,
    };

    /// <summary>Poll helper — waits until <paramref name="predicate"/> becomes true or times out.
    /// The debug flow inherently has async phases with no SignalR receiver in tests, so
    /// polling is the most pragmatic approach here rather than wiring up TaskCompletionSources
    /// in the test code.</summary>
    private static async Task WaitFor(Func<bool> predicate, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Predicate didn't become true within {timeoutMs}ms");
    }

    [Fact]
    public async Task BreakpointWithDebugEnabled_Pauses_ThenResumeContinues()
    {
        var wf = CreateWorkflow(BuildBreakpointWorkflow(breakpointOnFirstStep: false, breakpointOnSecondStep: true));
        _db.Workflows.Add(wf);
        await _db.SaveChangesAsync();

        // Fire-and-forget — the task blocks at the pause until we call Resume.
        var runTask = _engine.ExecuteAsync(wf, "debug", CancellationToken.None, debugEnabled: true);

        // Wait for the pause: the engine should pause at step-2.
        await WaitFor(() => _engine.GetPausedSteps(wf.Executions.Select(e => e.Id).FirstOrDefault()) is var _ &&
                             _db.WorkflowExecutions.AsNoTracking().FirstOrDefault() is { } exec &&
                             _engine.GetPausedSteps(exec.Id).Contains("step-2"));

        var execution = _db.WorkflowExecutions.AsNoTracking().First();
        _engine.GetPausedSteps(execution.Id).Should().Contain("step-2");

        // step-1 should be terminal (Succeeded) by now; step-2 is Paused.
        var step2Row = _db.StepExecutions.AsNoTracking().First(s => s.StepId == "step-2");
        step2Row.Status.Should().Be(ExecutionStatus.Paused);
        step2Row.VariablesSnapshot.Should().NotBeNull();
        step2Row.PausedAt.Should().NotBeNull();

        // Resume with Continue.
        _engine.Resume(execution.Id, "step-2", DebugResumeCommand.Continue, null).Should().BeTrue();

        var final = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        final.Status.Should().Be(ExecutionStatus.Succeeded);
        _capturedVariables.Should().NotBeNull();
    }

    [Fact]
    public async Task BreakpointSnapshot_RedactsSensitiveVariableNamesInStorageAndLiveEvent()
    {
        var wf = CreateWorkflow(BuildBreakpointWorkflow(breakpointOnFirstStep: false, breakpointOnSecondStep: true));
        _db.Workflows.Add(wf);
        await _db.SaveChangesAsync();

        var runTask = _engine.ExecuteAsync(
            wf,
            "debug",
            CancellationToken.None,
            inputParameters: new Dictionary<string, string>
            {
                ["dbPassword"] = "opaque-secret-value",
                ["serverName"] = "web01",
            },
            debugEnabled: true);

        await WaitFor(() => _db.WorkflowExecutions.AsNoTracking().FirstOrDefault() is { } exec
                            && _engine.GetPausedSteps(exec.Id).Contains("step-2"));

        var execution = _db.WorkflowExecutions.AsNoTracking().First();
        var step = _db.StepExecutions.AsNoTracking().Single(row => row.StepId == "step-2");
        using var snapshot = JsonDocument.Parse(step.VariablesSnapshot!);
        snapshot.RootElement.GetProperty("manual.dbPassword").GetString()
            .Should().Be(NodePilot.Engine.Security.OutputRedactor.Placeholder);
        snapshot.RootElement.GetProperty("manual.serverName").GetString().Should().Be("web01");
        step.VariablesSnapshot.Should().NotContain("opaque-secret-value");

        _notifier.Verify(notifier => notifier.StepPausedAsync(
            execution.Id,
            wf.Id,
            "step-2",
            It.IsAny<string?>(),
            It.Is<IReadOnlyDictionary<string, string>>(values =>
                values["manual.dbPassword"] == NodePilot.Engine.Security.OutputRedactor.Placeholder
                && values["manual.serverName"] == "web01"),
            It.IsAny<DateTime>(),
            It.IsAny<string>()), Times.Once);

        _engine.Resume(execution.Id, "step-2", DebugResumeCommand.Continue, null).Should().BeTrue();
        (await runTask.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(ExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task DebugDisabled_BreakpointIgnored_RunsToCompletion()
    {
        // Breakpoint is present in the JSON, but debugEnabled=false → the engine should ignore it.
        var wf = CreateWorkflow(BuildBreakpointWorkflow(breakpointOnFirstStep: true, breakpointOnSecondStep: true));
        _db.Workflows.Add(wf);
        await _db.SaveChangesAsync();

        var final = await _engine.ExecuteAsync(wf, "manual", CancellationToken.None, debugEnabled: false)
            .WaitAsync(TimeSpan.FromSeconds(3));

        final.Status.Should().Be(ExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task Resume_WithOverrides_InjectsIntoExecutorVariables()
    {
        var wf = CreateWorkflow(BuildBreakpointWorkflow(breakpointOnFirstStep: false, breakpointOnSecondStep: true));
        _db.Workflows.Add(wf);
        await _db.SaveChangesAsync();

        var runTask = _engine.ExecuteAsync(wf, "debug", CancellationToken.None, debugEnabled: true);

        await WaitFor(() => _db.WorkflowExecutions.AsNoTracking().FirstOrDefault() is { } exec &&
                             _engine.GetPausedSteps(exec.Id).Contains("step-2"));

        var execution = _db.WorkflowExecutions.AsNoTracking().First();
        _capturedVariables = null; // reset — only step-2's variables matter here
        _engine.Resume(execution.Id, "step-2", DebugResumeCommand.Continue,
            new Dictionary<string, string> { ["manual.injected"] = "hello" }).Should().BeTrue();

        var final = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        final.Status.Should().Be(ExecutionStatus.Succeeded);
        _capturedVariables.Should().NotBeNull();
        _capturedVariables!.Should().ContainKey("manual.injected").WhoseValue.Should().Be("hello");
    }

    [Fact]
    public async Task StopCommand_CancelsExecution()
    {
        var wf = CreateWorkflow(BuildBreakpointWorkflow(breakpointOnFirstStep: false, breakpointOnSecondStep: true));
        _db.Workflows.Add(wf);
        await _db.SaveChangesAsync();

        var runTask = _engine.ExecuteAsync(wf, "debug", CancellationToken.None, debugEnabled: true);

        await WaitFor(() => _db.WorkflowExecutions.AsNoTracking().FirstOrDefault() is { } exec &&
                             _engine.GetPausedSteps(exec.Id).Contains("step-2"));

        var execution = _db.WorkflowExecutions.AsNoTracking().First();
        _engine.Resume(execution.Id, "step-2", DebugResumeCommand.Stop, null).Should().BeTrue();

        var final = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        final.Status.Should().Be(ExecutionStatus.Cancelled);
    }

    /// <summary>Builds a two-node workflow whose second step has a literal breakpointCondition.
    /// Use this to verify the parser actually populates BreakpointCondition AND the engine's
    /// truthy/falsy gate honors it. A literal value isolates the gate logic from variable
    /// resolution.</summary>
    private static string BuildConditionalBreakpointWorkflow(string condition) =>
        "{\"nodes\":[" +
        "{\"id\":\"trigger-1\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"activityType\":\"manualTrigger\",\"config\":{}}}," +
        "{\"id\":\"step-1\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"activityType\":\"runScript\",\"config\":{}}}," +
        "{\"id\":\"step-2\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"activityType\":\"runScript\",\"breakpoint\":true,\"breakpointCondition\":\"" + condition + "\",\"config\":{}}}" +
        "],\"edges\":[{\"id\":\"te\",\"source\":\"trigger-1\",\"target\":\"step-1\"},{\"id\":\"e1\",\"source\":\"step-1\",\"target\":\"step-2\"}]}";

    [Fact]
    public async Task BreakpointCondition_TruthyValue_PausesAtBreakpoint()
    {
        // Regression: the parser must populate `breakpointCondition`, otherwise the gate
        // branch in WorkflowEngine.cs never fires and the feature silently stops working.
        var wf = CreateWorkflow(BuildConditionalBreakpointWorkflow("true"));
        _db.Workflows.Add(wf);
        await _db.SaveChangesAsync();

        var runTask = _engine.ExecuteAsync(wf, "debug", CancellationToken.None, debugEnabled: true);

        await WaitFor(() => _db.WorkflowExecutions.AsNoTracking().FirstOrDefault() is { } exec &&
                             _engine.GetPausedSteps(exec.Id).Contains("step-2"));

        var execution = _db.WorkflowExecutions.AsNoTracking().First();
        _engine.GetPausedSteps(execution.Id).Should().Contain("step-2");

        _engine.Resume(execution.Id, "step-2", DebugResumeCommand.Continue, null).Should().BeTrue();
        var final = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        final.Status.Should().Be(ExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task BreakpointCondition_FalsyValue_DoesNotPause()
    {
        // breakpoint:true + breakpointCondition resolved to "false" → engine skips the pause.
        // Without the parser fix, this test would still pass by accident (BreakpointCondition
        // =null means "always pause", so the test would wait for a pause and time out).
        // Combined with the truthy test above, this covers both code paths.
        var wf = CreateWorkflow(BuildConditionalBreakpointWorkflow("false"));
        _db.Workflows.Add(wf);
        await _db.SaveChangesAsync();

        var final = await _engine.ExecuteAsync(wf, "debug", CancellationToken.None, debugEnabled: true)
            .WaitAsync(TimeSpan.FromSeconds(3));

        final.Status.Should().Be(ExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task CancelDuringPause_UnblocksEngineCleanly()
    {
        // Regression: the cancel endpoint must not have to wait for the pause guard — it
        // has to actively release the pending TaskCompletionSource. Otherwise /cancel would
        // appear to hang forever.
        var wf = CreateWorkflow(BuildBreakpointWorkflow(breakpointOnFirstStep: false, breakpointOnSecondStep: true));
        _db.Workflows.Add(wf);
        await _db.SaveChangesAsync();

        var runTask = _engine.ExecuteAsync(wf, "debug", CancellationToken.None, debugEnabled: true);

        await WaitFor(() => _db.WorkflowExecutions.AsNoTracking().FirstOrDefault() is { } exec &&
                             _engine.GetPausedSteps(exec.Id).Contains("step-2"));

        var execution = _db.WorkflowExecutions.AsNoTracking().First();
        (await _engine.CancelAsync(execution.Id)).Should().BeTrue();

        var final = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        final.Status.Should().Be(ExecutionStatus.Cancelled);
    }
}
