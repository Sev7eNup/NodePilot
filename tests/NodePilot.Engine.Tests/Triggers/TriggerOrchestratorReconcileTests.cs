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
/// Reconcile-loop coverage for <see cref="TriggerOrchestrator"/>. We drive it through
/// <see cref="ScheduleTriggerSource"/> and stub Quartz with a Moq-based
/// <see cref="ISchedulerFactory"/>; ScheduleJob/DeleteJob call counts are the observable
/// signal that the orchestrator did or did not register/dispose a source. Every source type
/// (schedule/file/db/eventLog) is constructed with `new` inside the orchestrator from
/// root-resolved singletons, so none can be substituted from a test without refactoring —
/// dedicated unit tests for those live alongside the source classes themselves. Note that
/// the container below deliberately has NO ScheduleTriggerSource registration: the
/// orchestrator must not depend on one (see CreateSource).
/// </summary>
[Collection(ScheduleJobSlotCollection.Name)]
public class TriggerOrchestratorReconcileTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly NodePilotDbContext _db;
    private readonly ServiceProvider _services;
    private readonly Mock<IScheduler> _scheduler;
    private readonly Mock<ISchedulerFactory> _schedulerFactory;
    private readonly TriggerOrchestrator _orchestrator;
    private readonly TriggerHealthRegistry _healthRegistry = new();

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
            NullLogger<TriggerOrchestrator>.Instance,
            NodePilot.TestCommons.TestDatabaseAvailability.Available,
            _healthRegistry);
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
        // The orchestrator's own teardown lives in the BackgroundService loop, which these tests
        // never start — they drive SyncAsync directly. Every source a sync registered is still in
        // _active, holding a slot in ScheduleTriggerSource's process-static MaxActiveJobs counter.
        // Leaking those makes the cap-based assertions in ScheduleTriggerSourceTests fail or pass
        // purely by class order, which is why that flake never reproduced on demand.
        await _orchestrator.DisposeActiveSourcesAsync();
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

    /// <summary>
    /// The orchestrator must build its trigger sources itself instead of resolving them from the
    /// container: <see cref="ITriggerSource"/> is IAsyncDisposable, and a transient disposable
    /// pulled from the root provider stays tracked (= referenced, and disposed a second time at
    /// shutdown) for the entire process lifetime. This container has no ScheduleTriggerSource
    /// registration at all — registering the trigger still has to work.
    /// </summary>
    [Fact]
    public async Task SyncAsync_RegistersScheduleTrigger_WithoutContainerRegistration()
    {
        _services.GetService<ScheduleTriggerSource>().Should().BeNull(
            "the orchestrator must not depend on a container registration for its sources");
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

    // ---- health eviction: a source that died after starting must be re-created ----
    //
    // Driven through the SourceFactory seam with a fake, because no real source can be made to
    // report unhealthy on demand from a test — the one failure this covers (FileSystemWatcher
    // whose UNC share vanished) needs a genuine native-handle failure to reproduce.

    private sealed class FakeTriggerSource : ITriggerSource
    {
        public string ActivityType => "scheduleTrigger";
        public TriggerHealth HealthValue = TriggerHealth.Healthy;
        public bool ThrowOnHealthRead;
        public bool ThrowOnStart;
        public int DisposeCount;
        public int StartCount;

        public TriggerHealth Health => ThrowOnHealthRead
            ? throw new InvalidOperationException("Health was read for a source that is being removed anyway")
            : HealthValue;

        public Task StartAsync(TriggerContext context, CancellationToken ct)
        {
            StartCount++;
            return ThrowOnStart
                ? Task.FromException(new InvalidOperationException("simulated registration failure"))
                : Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Substitutes the source factory and records every instance it hands out.</summary>
    private List<FakeTriggerSource> UseFakeSources()
    {
        var created = new List<FakeTriggerSource>();
        _orchestrator.SourceFactory = _ =>
        {
            var src = new FakeTriggerSource();
            created.Add(src);
            return src;
        };
        return created;
    }

    [Fact]
    public async Task SyncAsync_DisposesAndRecreatesSource_WhenSourceReportsUnhealthy()
    {
        // Restart a missing source even when its stored config hash still matches.
        var created = UseFakeSources();
        await InsertWorkflowAsync(DefinitionWithSchedule("trg1", "0 0/1 * * * ?"));

        await _orchestrator.SyncAsync(CancellationToken.None);
        created.Should().HaveCount(1);

        created[0].HealthValue = TriggerHealth.Faulted("share vanished");
        await _orchestrator.SyncAsync(CancellationToken.None);

        created[0].DisposeCount.Should().Be(1);
        created.Should().HaveCount(2);
        created[1].StartCount.Should().Be(1);
        created[1].DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task SyncAsync_DoesNotEvict_WhenSourceReportsHealthy()
    {
        // No-churn pin: eviction must key off the source's verdict, not run every tick.
        var created = UseFakeSources();
        await InsertWorkflowAsync(DefinitionWithSchedule("trg1", "0 0/1 * * * ?"));

        await _orchestrator.SyncAsync(CancellationToken.None);
        await _orchestrator.SyncAsync(CancellationToken.None);
        await _orchestrator.SyncAsync(CancellationToken.None);

        created.Should().HaveCount(1);
        created[0].DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task SyncAsync_BacksOffRecreate_WhenRegistrationAfterEvictionFails()
    {
        // The whole point of routing eviction back through the add-loop is inheriting its
        // exponential backoff: while the share stays gone, re-registration must not be retried
        // on every 5-second tick.
        var created = UseFakeSources();
        await InsertWorkflowAsync(DefinitionWithSchedule("trg1", "0 0/1 * * * ?"));

        await _orchestrator.SyncAsync(CancellationToken.None);
        created[0].HealthValue = TriggerHealth.Faulted("share vanished");

        _orchestrator.SourceFactory = _ =>
        {
            var src = new FakeTriggerSource { ThrowOnStart = true };
            created.Add(src);
            return src;
        };

        await _orchestrator.SyncAsync(CancellationToken.None); // evicts, re-registration fails -> backoff
        await _orchestrator.SyncAsync(CancellationToken.None); // still inside the 5s cool-down

        created.Should().HaveCount(2);
        created[1].StartCount.Should().Be(1);
    }

    [Fact]
    public async Task SyncAsync_RecordsUnhealthyTrigger_InHealthRegistry()
    {
        // What makes the outage alertable instead of silent: the sync pass keeps succeeding while
        // the trigger is broken, so nothing else in the system notices. The entry is written by
        // the failed re-registration — an eviction whose retry succeeds was never an outage.
        var created = UseFakeSources();
        var wf = await InsertWorkflowAsync(DefinitionWithSchedule("trg1", "0 0/1 * * * ?"));

        await _orchestrator.SyncAsync(CancellationToken.None);
        _healthRegistry.Snapshot().Should().BeEmpty();

        created[0].HealthValue = TriggerHealth.Faulted("share vanished");
        _orchestrator.SourceFactory = _ =>
        {
            var src = new FakeTriggerSource { ThrowOnStart = true };
            created.Add(src);
            return src;
        };
        await _orchestrator.SyncAsync(CancellationToken.None);

        var entry = _healthRegistry.Snapshot().Should().ContainSingle().Subject;
        entry.WorkflowId.Should().Be(wf.Id);
        entry.NodeId.Should().Be("trg1");
        entry.TriggerType.Should().Be("scheduleTrigger");
        entry.ConsecutiveFailures.Should().Be(1);
        entry.Reason.Should().Contain("simulated registration failure");
    }

    [Fact]
    public async Task SyncAsync_ClearsHealthRegistry_OnceTriggerRegistersAgain()
    {
        var created = UseFakeSources();
        await InsertWorkflowAsync(DefinitionWithSchedule("trg1", "0 0/1 * * * ?"));

        await _orchestrator.SyncAsync(CancellationToken.None);
        created[0].HealthValue = TriggerHealth.Faulted("share vanished");
        await _orchestrator.SyncAsync(CancellationToken.None); // evicted + re-registered in one pass

        _healthRegistry.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task SyncAsync_ClearsHealthRegistry_WhenWorkflowIsDisabled()
    {
        // A trigger that no longer exists is not a broken trigger — it must stop alerting.
        var created = UseFakeSources();
        var wf = await InsertWorkflowAsync(DefinitionWithSchedule("trg1", "0 0/1 * * * ?"));

        await _orchestrator.SyncAsync(CancellationToken.None);
        created[0].HealthValue = TriggerHealth.Faulted("share vanished");
        _orchestrator.SourceFactory = _ =>
        {
            var src = new FakeTriggerSource { ThrowOnStart = true };
            created.Add(src);
            return src;
        };
        await _orchestrator.SyncAsync(CancellationToken.None);
        _healthRegistry.Snapshot().Should().HaveCount(1);

        wf.IsEnabled = false;
        await _db.SaveChangesAsync();
        await _orchestrator.SyncAsync(CancellationToken.None);

        _healthRegistry.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task SyncAsync_DoesNotReadHealth_ForSourcesBeingRemovedAnyway()
    {
        // Health is contractually cheap, but asking a source we are already disposing is pointless
        // and would couple removal to a source's ability to answer. The fake throws to pin it.
        var created = UseFakeSources();
        var wf = await InsertWorkflowAsync(DefinitionWithSchedule("trg1", "0 0/1 * * * ?"));

        await _orchestrator.SyncAsync(CancellationToken.None);
        created[0].ThrowOnHealthRead = true;

        wf.IsEnabled = false;
        await _db.SaveChangesAsync();

        var act = () => _orchestrator.SyncAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        created[0].DisposeCount.Should().Be(1);
    }
}
