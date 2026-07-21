using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine;
using NodePilot.Engine.Tests.Helpers;
using Xunit;
using NodePilot.Core.Telemetry;

namespace NodePilot.Engine.Tests.Telemetry;

/// <summary>
/// Asserts that the engine emits its OpenTelemetry metrics and trace spans every time a
/// workflow runs (these were added in two separate work phases, referred to here as
/// "Phase-3 metrics" and "Phase-2 spans"). Uses BCL listeners (no OpenTelemetry SDK
/// dependency) so the tests stay fast and deterministic.
///
/// Pinned to a non-parallel collection: <see cref="EngineMetrics"/> is a process-wide
/// static Meter. If another test class (e.g. WorkflowEngineTests) runs concurrently,
/// its runScript step emissions leak into this collector (workflow_id is not part of
/// the <c>nodepilot.steps.executed</c> tag set), inflating the count.
/// </summary>
[Collection("SerialEngineTests")]
public class TelemetryEmissionTests
{
    private readonly NodePilotDbContext _db;
    private readonly Mock<IActivityExecutor> _mockExecutor;
    private readonly ActivityRegistry _registry;
    private readonly WorkflowEngine _engine;

    public TelemetryEmissionTests()
    {
        _mockExecutor = new Mock<IActivityExecutor>();
        _mockExecutor.Setup(e => e.ActivityType).Returns("runScript");
        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult { Success = true, Output = "OK" });

        // Execution roots are trigger-only: every runnable fixture must be rooted at a
        // manualTrigger node. This mock is immediate + side-effect-free so it never
        // perturbs the runScript step under test.
        var manualTriggerExecutor = new Mock<IActivityExecutor>();
        manualTriggerExecutor.Setup(e => e.ActivityType).Returns("manualTrigger");
        manualTriggerExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult { Success = true, Output = "{}" });

        _registry = new ActivityRegistry(new[] { _mockExecutor.Object, manualTriggerExecutor.Object });
        (_db, var sp, _) = TestDbContext.CreateWithScopedServices(_registry);
        var logger = NullLogger<WorkflowEngine>.Instance;
        var notifier = new Mock<IExecutionNotifier>();
        _engine = new WorkflowEngine(_db, _registry, logger, sp, notifier.Object);
    }

    private static Workflow CreateWorkflow(string definitionJson) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Telemetry-Test",
        DefinitionJson = definitionJson,
    };

    private const string SingleNode =
        "{\"nodes\":[" +
        "{\"id\":\"trigger-1\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"activityType\":\"manualTrigger\",\"config\":{}}}," +
        "{\"id\":\"step-1\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"activityType\":\"runScript\",\"config\":{}}}" +
        "],\"edges\":[{\"id\":\"te\",\"source\":\"trigger-1\",\"target\":\"step-1\"}]}";

    [Fact]
    public async Task ExecuteAsync_SuccessfulRun_EmitsExecutionAndStepMetrics()
    {
        using var metrics = new MetricCollector(TelemetryConstants.Meters.Engine);

        var workflow = CreateWorkflow(SingleNode);
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "manual", CancellationToken.None);
        execution.Status.Should().Be(ExecutionStatus.Succeeded);

        metrics.SumLong("nodepilot.executions.started",
            ("workflow_id", workflow.Id.ToString())).Should().Be(1);

        metrics.SumLong("nodepilot.executions.completed",
            ("workflow_id", workflow.Id.ToString()),
            ("status", "Succeeded")).Should().Be(1);

        metrics.MaxDouble("nodepilot.execution.duration",
            ("status", "Succeeded")).Should().NotBeNull().And.Subject.Should().BeGreaterThan(0);

        metrics.SumLong("nodepilot.steps.executed",
            ("activity_type", "runScript"),
            ("status", "Succeeded")).Should().Be(1);

        metrics.MaxDouble("nodepilot.step.duration",
            ("activity_type", "runScript"),
            ("status", "Succeeded")).Should().NotBeNull();

        metrics.SumLong("nodepilot.db.save_changes",
            ("operation", "execution.start"),
            ("status", "success")).Should().Be(1);

        // Two step-terminal saves: the manualTrigger root step + the runScript step.
        metrics.SumLong("nodepilot.db.save_changes",
            ("operation", "step.terminal"),
            ("status", "success")).Should().Be(2);

        metrics.SumLong("nodepilot.db.save_changes",
            ("operation", "execution.terminal"),
            ("status", "success")).Should().Be(1);

        metrics.MaxDouble("nodepilot.db.save_changes.duration",
            ("operation", "step.terminal"),
            ("status", "success")).Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_PendingOverride_DefersRunningExecutionSave()
    {
        using var metrics = new MetricCollector(TelemetryConstants.Meters.Engine);

        var workflow = CreateWorkflow(SingleNode);
        var executionId = Guid.NewGuid();
        _db.Workflows.Add(workflow);
        _db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Pending,
            StartedAt = DateTime.UtcNow,
            TriggeredBy = "manual",
        });
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(
            workflow,
            "manual",
            CancellationToken.None,
            executionIdOverride: executionId);

        execution.Status.Should().Be(ExecutionStatus.Succeeded);
        metrics.SumLong("nodepilot.db.save_changes",
            ("operation", "execution.start"),
            ("status", "success")).Should().Be(0);
        metrics.SumLong("nodepilot.db.save_changes",
            ("operation", "execution.terminal"),
            ("status", "success")).Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulRun_ActiveGaugeReturnsToZero()
    {
        using var metrics = new MetricCollector(TelemetryConstants.Meters.Engine);

        var workflow = CreateWorkflow(SingleNode);
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        await _engine.ExecuteAsync(workflow, "manual", CancellationToken.None);

        // UpDownCounter emits +1 then -1 — sum of all recorded values for this workflow
        // must be zero after the run completes.
        metrics.SumLong("nodepilot.executions.active",
            ("workflow_id", workflow.Id.ToString())).Should().Be(0);

        metrics.Count("nodepilot.executions.active",
            ("workflow_id", workflow.Id.ToString())).Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_FailedStep_EmitsCompletedWithFailedStatus()
    {
        using var metrics = new MetricCollector(TelemetryConstants.Meters.Engine);

        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult { Success = false, ErrorOutput = "boom" });

        var workflow = CreateWorkflow(SingleNode);
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "manual", CancellationToken.None);
        execution.Status.Should().Be(ExecutionStatus.Failed);

        metrics.SumLong("nodepilot.executions.completed",
            ("status", "Failed")).Should().Be(1);

        metrics.SumLong("nodepilot.steps.executed",
            ("activity_type", "runScript"),
            ("status", "Failed")).Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_Cancelled_EmitsCancellationsCounter()
    {
        using var metrics = new MetricCollector(TelemetryConstants.Meters.Engine);

        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .Returns<StepExecutionContext, JsonElement, CancellationToken>(async (_, _, token) =>
            {
                await Task.Delay(200, token);
                return new ActivityResult { Success = true };
            });

        var workflow = CreateWorkflow(SingleNode);
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        using var cts = new CancellationTokenSource();
        var runTask = _engine.ExecuteAsync(workflow, "manual", cts.Token);
        await Task.Delay(25);
        cts.Cancel();
        var execution = await runTask;

        execution.Status.Should().Be(ExecutionStatus.Cancelled);
        metrics.SumLong("nodepilot.execution.cancellations").Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task ExecuteAsync_RecordsNodesExecutedAndSkippedHistograms()
    {
        using var metrics = new MetricCollector(TelemetryConstants.Meters.Engine);

        var workflow = CreateWorkflow(SingleNode);
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        await _engine.ExecuteAsync(workflow, "manual", CancellationToken.None);

        var wfTag = ("workflow_id", (object?)workflow.Id.ToString());
        // nodes_executed/nodes_skipped are each Recorded once per execution (Count == 1),
        // with the node total as the value. Two nodes now execute (manualTrigger + runScript)
        // so the recorded value is 2.
        metrics.Count("nodepilot.execution.nodes_executed", wfTag).Should().Be(1);
        metrics.Count("nodepilot.execution.nodes_skipped", wfTag).Should().Be(1);
        metrics.MaxDouble("nodepilot.execution.nodes_executed", wfTag).Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_ProducesRootSpanWithExecutionAttributes()
    {
        using var traces = new TraceCollector(
            TelemetryConstants.Sources.Engine,
            TelemetryConstants.Sources.EngineActivities);

        var workflow = CreateWorkflow(SingleNode);
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "manual", CancellationToken.None);

        var root = traces.Where("workflow.execute")
            .SingleOrDefault(a => a.GetTagItem(TelemetryConstants.Attributes.ExecutionId)?.ToString() == execution.Id.ToString());
        root.Should().NotBeNull();
        root!.GetTagItem(TelemetryConstants.Attributes.WorkflowId).Should().Be(workflow.Id.ToString());
        root.GetTagItem(TelemetryConstants.Attributes.ExecutionTrigger).Should().Be("manual");
        root.GetTagItem(TelemetryConstants.Attributes.ExecutionStatus).Should().Be("Succeeded");
    }

    [Fact]
    public async Task ExecuteAsync_ProducesNestedStepAndActivitySpans()
    {
        using var traces = new TraceCollector(
            TelemetryConstants.Sources.Engine,
            TelemetryConstants.Sources.EngineActivities);

        var workflow = CreateWorkflow(SingleNode);
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "manual", CancellationToken.None);
        var executionId = execution.Id.ToString();

        // Two workflow.step spans now exist (manualTrigger root + runScript); narrow to runScript.
        var step = traces.Where("workflow.step")
            .SingleOrDefault(a =>
                a.GetTagItem(TelemetryConstants.Attributes.ExecutionId)?.ToString() == executionId &&
                a.GetTagItem(TelemetryConstants.Attributes.StepActivityType)?.ToString() == "runScript");
        step.Should().NotBeNull();
        step!.GetTagItem(TelemetryConstants.Attributes.StepId).Should().Be("step-1");
        step.GetTagItem(TelemetryConstants.Attributes.StepActivityType).Should().Be("runScript");

        var activity = traces.Where("activity.runScript")
            .SingleOrDefault(a => a.Parent == step);
        activity.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ExecutionRowCarriesTraceAndSpanIds()
    {
        using var traces = new TraceCollector(TelemetryConstants.Sources.Engine);

        var workflow = CreateWorkflow(SingleNode);
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "manual", CancellationToken.None);

        execution.TraceId.Should().NotBeNullOrEmpty();
        execution.SpanId.Should().NotBeNullOrEmpty();

        var root = traces.Where("workflow.execute")
            .SingleOrDefault(a => a.GetTagItem(TelemetryConstants.Attributes.ExecutionId)?.ToString() == execution.Id.ToString());
        root.Should().NotBeNull();
        root!.TraceId.ToString().Should().Be(execution.TraceId);
        root.SpanId.ToString().Should().Be(execution.SpanId);
    }
}
