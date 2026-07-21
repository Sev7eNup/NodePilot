using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Core.ExecutionDispatch;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Tests.Helpers;
using NodePilot.Scheduler;
using Xunit;

namespace NodePilot.Engine.Tests.Scheduler;

/// <summary>
/// Exercises the <see cref="TriggerOrchestrator"/> sync + fire paths through their
/// internal entry points to assert the scheduler's OpenTelemetry metric and span emission
/// (added in a work phase referred to here as "Phase-4"). The real orchestrator
/// runs on a timer; tests invoke <c>SyncAsync</c> / <c>FireAsync</c> directly.
/// </summary>
public class SchedulerMetricsTests
{
    private readonly NodePilotDbContext _db;
    private readonly Mock<IWorkflowEngine> _engine;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServiceProvider _rootServices;
    private readonly TriggerOrchestrator _orchestrator;

    public SchedulerMetricsTests()
    {
        _db = TestDbContext.Create();
        _engine = new Mock<IWorkflowEngine>();
        _engine.Setup(e => e.ExecuteAsync(
                It.IsAny<Workflow>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<Dictionary<string, string>?>(),
                It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>()))
            .ReturnsAsync(new WorkflowExecution());

        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton(_engine.Object);
        services.AddSingleton<IExecutionDispatchQueue, NoopExecutionDispatchQueue>();
        services.AddSingleton<IWorkflowExecutionDispatcher, NoopWorkflowExecutionDispatcher>();
        var provider = services.BuildServiceProvider();
        _rootServices = provider;
        _scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        _orchestrator = new TriggerOrchestrator(_scopeFactory, _rootServices,
            new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            NullLogger<TriggerOrchestrator>.Instance);
    }

    private sealed class NoopExecutionDispatchQueue : IExecutionDispatchQueue
    {
        public ValueTask EnqueueAsync(
            Func<CancellationToken, Task> workItem,
            CancellationToken ct,
            ExecutionDispatchPriority priority = ExecutionDispatchPriority.Normal)
            => ValueTask.CompletedTask;
    }

    private sealed class NoopWorkflowExecutionDispatcher : IWorkflowExecutionDispatcher
    {
        public Task<WorkflowExecution> DispatchAsync(WorkflowDispatchIntent intent, CancellationToken ct)
            => Task.FromResult(new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                WorkflowId = intent.WorkflowId,
                Status = ExecutionStatus.Pending,
                StartedAt = DateTime.UtcNow,
                TriggeredBy = intent.TriggeredBy,
            });
    }

    [Fact]
    public async Task FireAsync_RecordsTriggersFiredCounter()
    {
        using var metrics = new MetricCollector("NodePilot.Scheduler");

        var wf = new Workflow { Id = Guid.NewGuid(), Name = "wf", DefinitionJson = "{}", IsEnabled = true };
        _db.Workflows.Add(wf);
        await _db.SaveChangesAsync();

        await _orchestrator.FireAsync(wf.Id, "scheduleTrigger", new Dictionary<string, string>());

        metrics.SumLong("nodepilot.triggers.fired",
            ("trigger_type", "scheduleTrigger"),
            ("workflow_id", wf.Id.ToString())).Should().Be(1);
    }

    [Fact]
    public async Task FireAsync_DisabledWorkflow_StillEmitsCounterButSkipsEngine()
    {
        using var metrics = new MetricCollector("NodePilot.Scheduler");

        var wf = new Workflow { Id = Guid.NewGuid(), Name = "wf", DefinitionJson = "{}", IsEnabled = false };
        _db.Workflows.Add(wf);
        await _db.SaveChangesAsync();

        await _orchestrator.FireAsync(wf.Id, "scheduleTrigger", new Dictionary<string, string>());

        // We count the fire attempt even when the workflow can't run — otherwise a trigger
        // storm against a disabled workflow stays invisible in Prometheus.
        metrics.SumLong("nodepilot.triggers.fired",
            ("trigger_type", "scheduleTrigger")).Should().BeGreaterThanOrEqualTo(1);

        _engine.Verify(e => e.ExecuteAsync(
            It.IsAny<Workflow>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<Dictionary<string, string>?>(),
            It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>()),
            Times.Never);
    }

    [Fact]
    public async Task FireAsync_ProducesTriggerFireSpan()
    {
        using var traces = new TraceCollector("NodePilot.Scheduler");

        var wf = new Workflow { Id = Guid.NewGuid(), Name = "wf", DefinitionJson = "{}", IsEnabled = true };
        _db.Workflows.Add(wf);
        await _db.SaveChangesAsync();

        await _orchestrator.FireAsync(wf.Id, "eventLogTrigger", new Dictionary<string, string>());

        var fire = traces.Where("trigger.fire")
            .SingleOrDefault(a => a.GetTagItem("nodepilot.workflow.id")?.ToString() == wf.Id.ToString());
        fire.Should().NotBeNull();
        fire!.GetTagItem("nodepilot.trigger.type").Should().Be("eventLogTrigger");
    }

    [Fact]
    public async Task SyncAsync_EmitsSyncDurationHistogram()
    {
        using var metrics = new MetricCollector("NodePilot.Scheduler");

        await _orchestrator.SyncAsync(CancellationToken.None);

        metrics.Count("nodepilot.trigger.orchestrator.sync.duration").Should().BeGreaterThanOrEqualTo(1);
    }
}
