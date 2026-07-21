using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Core.ExecutionDispatch;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Scheduler;
using NodePilot.Scheduler.Sources;
using Quartz;
using Xunit;

namespace NodePilot.Engine.Tests.Triggers;

/// <summary>
/// Reconcile-loop coverage for <see cref="TriggerOrchestrator"/>. We use the only
/// DI-resolvable source (<see cref="ScheduleTriggerSource"/>) and stub Quartz with a
/// Moq-based <see cref="ISchedulerFactory"/>; ScheduleJob/DeleteJob call counts are the
/// observable signal that the orchestrator did or did not register/dispose a source.
/// The other source types (file/db/eventLog) are constructed with `new` inside the
/// orchestrator, so they cannot be substituted from a test without refactoring -
/// dedicated unit tests for those live alongside the source classes themselves.
/// </summary>
public class TriggerOrchestratorReconcileTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly NodePilotDbContext _db;
    private readonly ServiceProvider _services;
    private readonly Mock<IScheduler> _scheduler;
    private readonly Mock<ISchedulerFactory> _schedulerFactory;
    private readonly TriggerOrchestrator _orchestrator;

    public TriggerOrchestratorReconcileTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _scheduler = new Mock<IScheduler>();
        _scheduler.Setup(s => s.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTimeOffset.UtcNow);
        _scheduler.Setup(s => s.DeleteJob(It.IsAny<JobKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _schedulerFactory = new Mock<ISchedulerFactory>();
        _schedulerFactory.Setup(f => f.GetScheduler(It.IsAny<CancellationToken>()))
            .ReturnsAsync(_scheduler.Object);
        _schedulerFactory.Setup(f => f.GetScheduler(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_scheduler.Object);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Bypass the "cron must fire at least every N seconds" guard so we can use
            // a one-minute cron without worrying about the 60s default.
            ["Trigger:Schedule:MinIntervalSeconds"] = "1",
        }).Build();

        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(opts => opts.UseSqlite(_connection));
        services.AddSingleton(_schedulerFactory.Object);
        services.AddSingleton<IConfiguration>(config);
        services.AddTransient<ScheduleTriggerSource>();
        services.AddSingleton<IWorkflowExecutionDispatcher, NoopWorkflowExecutionDispatcher>();
        // FireAsync resolves IWorkflowEngine from the per-tick scope. We never want a real
        // engine touched in these tests - reconcile tests stop before fire, suppression tests
        // exit before ExecuteAsync. A Mock satisfies the GetRequiredService contract.
        services.AddSingleton(Mock.Of<IWorkflowEngine>());
        // AppendSuppressionAudit pulls the stager from the per-tick scope so audit-row
        // construction goes through the same redaction + cap pipeline as every other
        // audit path. Tests need to register a real (redactor-less) stager — the entries
        // still get persisted, they just don't apply regex-based redaction.
        services.AddSingleton<NodePilot.Core.Audit.IAuditStager, NodePilot.Core.Audit.AuditStager>();
        services.AddLogging();
        _services = services.BuildServiceProvider();

        _db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _orchestrator = new TriggerOrchestrator(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services,
            new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            NullLogger<TriggerOrchestrator>.Instance);
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

    public async ValueTask DisposeAsync()
    {
        _db.Dispose();
        await _services.DisposeAsync();
        _connection.Dispose();
    }

    private static string DefinitionWithSchedule(string nodeId, string cron) =>
        $$"""
        {
          "nodes": [
            { "id": "{{nodeId}}", "type": "trigger", "data": { "activityType": "scheduleTrigger", "config": { "cronExpression": "{{cron}}" } } }
          ],
          "edges": []
        }
        """;

    private static string DefinitionWithDisabledSchedule(string nodeId, string cron) =>
        $$"""
        {
          "nodes": [
            { "id": "{{nodeId}}", "type": "trigger", "data": { "activityType": "scheduleTrigger", "disabled": true, "config": { "cronExpression": "{{cron}}" } } }
          ],
          "edges": []
        }
        """;

    private async Task<Workflow> InsertWorkflowAsync(string definition, bool enabled = true)
    {
        var wf = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "test-wf-" + Guid.NewGuid().ToString("N")[..8],
            DefinitionJson = definition,
            IsEnabled = enabled,
            Version = 1,
        };
        _db.Workflows.Add(wf);
        await _db.SaveChangesAsync();
        return wf;
    }

    [Fact]
    public async Task SyncAsync_RegistersSource_ForEnabledScheduleTrigger()
    {
        await InsertWorkflowAsync(DefinitionWithSchedule("trg1", "0 0/1 * * * ?"));

        await _orchestrator.SyncAsync(CancellationToken.None);

        _scheduler.Verify(s => s.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SyncAsync_DoesNotRegisterSource_ForDisabledScheduleTrigger()
    {
        await InsertWorkflowAsync(DefinitionWithDisabledSchedule("trg1", "0 0/1 * * * ?"));

        await _orchestrator.SyncAsync(CancellationToken.None);

        _scheduler.Verify(s => s.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SyncAsync_DoesNotReregister_WhenConfigUnchanged()
    {
        await InsertWorkflowAsync(DefinitionWithSchedule("trg1", "0 0/1 * * * ?"));

        await _orchestrator.SyncAsync(CancellationToken.None);
        await _orchestrator.SyncAsync(CancellationToken.None);

        _scheduler.Verify(s => s.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _scheduler.Verify(s => s.DeleteJob(It.IsAny<JobKey>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SyncAsync_DisposesSource_WhenWorkflowDisabled()
    {
        var wf = await InsertWorkflowAsync(DefinitionWithSchedule("trg1", "0 0/1 * * * ?"));
        await _orchestrator.SyncAsync(CancellationToken.None);

        wf.IsEnabled = false;
        wf.Version++;
        await _db.SaveChangesAsync();
        await _orchestrator.SyncAsync(CancellationToken.None);

        _scheduler.Verify(s => s.DeleteJob(It.IsAny<JobKey>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task SyncAsync_DisposesSource_WhenWorkflowDeleted()
    {
        var wf = await InsertWorkflowAsync(DefinitionWithSchedule("trg1", "0 0/1 * * * ?"));
        await _orchestrator.SyncAsync(CancellationToken.None);

        _db.Workflows.Remove(wf);
        await _db.SaveChangesAsync();
        await _orchestrator.SyncAsync(CancellationToken.None);

        _scheduler.Verify(s => s.DeleteJob(It.IsAny<JobKey>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task SyncAsync_DisposesAndRecreatesSource_WhenTriggerConfigChanges()
    {
        var wf = await InsertWorkflowAsync(DefinitionWithSchedule("trg1", "0 0/1 * * * ?"));
        await _orchestrator.SyncAsync(CancellationToken.None);

        wf.DefinitionJson = DefinitionWithSchedule("trg1", "0 0/2 * * * ?");
        wf.Version++;
        await _db.SaveChangesAsync();
        await _orchestrator.SyncAsync(CancellationToken.None);

        _scheduler.Verify(s => s.DeleteJob(It.IsAny<JobKey>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        _scheduler.Verify(s => s.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task SyncAsync_AppendsSuppressionAudit_WhenFiringDisabledWorkflow()
    {
        var wf = await InsertWorkflowAsync(DefinitionWithSchedule("trg1", "0 0/1 * * * ?"));
        wf.IsEnabled = false;
        await _db.SaveChangesAsync();

        await _orchestrator.FireAsync(wf.Id, "scheduleTrigger", new Dictionary<string, string>());

        var audits = await _db.AuditLog
            .Where(a => a.ResourceId == wf.Id && a.Action == "TRIGGER_FIRE_SUPPRESSED")
            .ToListAsync();
        audits.Should().HaveCount(1);
        audits[0].Details.Should().Contain("workflow_disabled");
    }

    [Fact]
    public async Task SyncAsync_AppendsSuppressionAudit_WhenFiringMissingWorkflow()
    {
        var ghostId = Guid.NewGuid();

        await _orchestrator.FireAsync(ghostId, "scheduleTrigger", new Dictionary<string, string>());

        var audits = await _db.AuditLog
            .Where(a => a.ResourceId == ghostId && a.Action == "TRIGGER_FIRE_SUPPRESSED")
            .ToListAsync();
        audits.Should().HaveCount(1);
        audits[0].Details.Should().Contain("workflow_deleted");
    }
}
