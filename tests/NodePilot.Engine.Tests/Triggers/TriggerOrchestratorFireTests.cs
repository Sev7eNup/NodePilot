using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.ExecutionDispatch;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Availability;
using NodePilot.Scheduler;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Engine.Tests.Triggers;

/// <summary>
/// The fire path of <see cref="TriggerOrchestrator"/>: what happens between "a source
/// delivered a fire" and "an execution was dispatched". Every early exit here writes a
/// distinct audit code so the operator can tell a maintenance-window suppression apart from a
/// fire against a disabled workflow — that distinction is the whole point of the two separate
/// audit helpers, so it is asserted rather than just "no execution was created".
/// </summary>
public sealed class TriggerOrchestratorFireTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly NodePilotDbContext _db;
    private readonly ServiceProvider _services;
    private readonly RecordingDispatcher _dispatcher = new();
    private readonly StubMaintenanceWindowEvaluator _maintenance = new();

    public TriggerOrchestratorFireTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(opts => opts.UseSqlite(_connection));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
        services.AddSingleton<IWorkflowExecutionDispatcher>(_dispatcher);
        services.AddSingleton<IMaintenanceWindowEvaluator>(_maintenance);
        services.AddSingleton(Mock.Of<IWorkflowEngine>());
        services.AddSingleton<IAuditStager, AuditStager>();
        services.AddLogging();
        _services = services.BuildServiceProvider();

        _db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync()
    {
        _db.Dispose();
        await _services.DisposeAsync();
        _connection.Dispose();
    }

    // ---------------------------------------------------------------- happy path

    [Fact]
    public async Task FireAsync_EnabledWorkflow_DispatchesAnExecution()
    {
        var workflowId = SeedWorkflow(enabled: true);

        await Orchestrator().FireAsync(workflowId, "scheduleTrigger", []);

        _dispatcher.Intents.Should().ContainSingle()
            .Which.WorkflowId.Should().Be(workflowId);
    }

    [Fact]
    public async Task FireAsync_ForwardsTheTriggerTypeAsTheTriggeredBySource()
    {
        var workflowId = SeedWorkflow(enabled: true);

        await Orchestrator().FireAsync(workflowId, "fileWatcherTrigger", []);

        _dispatcher.Intents.Single().TriggeredBy.Should().Contain("fileWatcherTrigger");
    }

    [Fact]
    public async Task FireAsync_ParametersAreSnapshottedCaseInsensitively()
    {
        var workflowId = SeedWorkflow(enabled: true);

        await Orchestrator().FireAsync(
            workflowId, "scheduleTrigger", new Dictionary<string, string> { ["FileName"] = "a.txt" });

        var parameters = _dispatcher.Intents.Single().Parameters!;
        parameters.Should().ContainKey("filename",
            "trigger params are matched case-insensitively downstream in the data bus");
    }

    [Fact]
    public async Task AdmitFireAsync_ReplayedEvent_IsDispatchedExactlyOnceAndAdvancesCheckpoint()
    {
        var workflowId = SeedWorkflow(enabled: true);
        var orchestrator = Orchestrator();
        var signal = new TriggerSignal("schedule:638919936000000000", "2026-08-27T10:00:00Z", []);

        var first = await orchestrator.AdmitFireAsync(
            workflowId, "schedule-node", "scheduleTrigger", "config-v1", signal);
        var replay = await orchestrator.AdmitFireAsync(
            workflowId, "schedule-node", "scheduleTrigger", "config-v1", signal);

        first.Should().BeTrue();
        replay.Should().BeTrue("the persisted receipt acknowledges a source retry");
        _dispatcher.Intents.Should().ContainSingle();
        _db.ChangeTracker.Clear();
        (await _db.TriggerDeliveryReceipts.AsNoTracking().ToListAsync()).Should().ContainSingle()
            .Which.EventKey.Should().Be(signal.EventKey);
        (await _db.TriggerDeliveryCheckpoints.AsNoTracking().SingleAsync()).Position
            .Should().Be(signal.Position);
    }

    [Fact]
    public async Task Checkpoint_ConfigurationChange_StartsFromAFreshBaseline()
    {
        var workflowId = SeedWorkflow(enabled: true);
        var orchestrator = Orchestrator();
        var oldSignal = new TriggerSignal("database:old", "old-sentinel", []);
        (await orchestrator.AdmitFireAsync(
            workflowId, "database-node", "databaseTrigger", "config-v1", oldSignal)).Should().BeTrue();

        (await orchestrator.ReadCheckpointAsync(workflowId, "database-node", "config-v2"))
            .Should().BeNull("a changed query or connection must not compare against the old sentinel");
        var newBaseline = new TriggerCheckpoint("new-sentinel", "database-seed:new");
        (await orchestrator.InitializeCheckpointAsync(
            workflowId, "database-node", "databaseTrigger", "config-v2", newBaseline)).Should().BeTrue();

        (await orchestrator.ReadCheckpointAsync(workflowId, "database-node", "config-v2"))
            .Should().Be(newBaseline);
    }

    [Fact]
    public async Task AdmitFireAsync_DispatchFailure_RollsBackReceiptAndCanBeRetried()
    {
        var workflowId = SeedWorkflow(enabled: true);
        var orchestrator = Orchestrator();
        var signal = new TriggerSignal("database:change-42", "42", []);
        _dispatcher.Failure = new InvalidOperationException("simulated dispatch failure");

        var failed = await orchestrator.AdmitFireAsync(
            workflowId, "database-node", "databaseTrigger", "config-v1", signal);

        failed.Should().BeFalse();
        _db.ChangeTracker.Clear();
        (await _db.TriggerDeliveryReceipts.AsNoTracking().CountAsync()).Should().Be(0);
        (await _db.TriggerDeliveryCheckpoints.AsNoTracking().CountAsync()).Should().Be(0);

        _dispatcher.Failure = null;
        var retried = await orchestrator.AdmitFireAsync(
            workflowId, "database-node", "databaseTrigger", "config-v1", signal);

        retried.Should().BeTrue();
        _dispatcher.Intents.Should().ContainSingle();
    }

    // ---------------------------------------------------------------- suppression

    [Fact]
    public async Task FireAsync_DisabledWorkflow_DoesNotDispatchAndAuditsTheSuppression()
    {
        var workflowId = SeedWorkflow(enabled: false);

        await Orchestrator().FireAsync(workflowId, "scheduleTrigger", []);

        _dispatcher.Intents.Should().BeEmpty();
        (await AuditActionsAsync()).Should().Contain(action => action.Contains("SUPPRESS", StringComparison.OrdinalIgnoreCase)
                                                              || action.Contains("TRIGGER", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FireAsync_DeletedWorkflow_DoesNotThrowAndStillAudits()
    {
        await Orchestrator().FireAsync(Guid.NewGuid(), "scheduleTrigger", []);

        _dispatcher.Intents.Should().BeEmpty();
        (await AuditActionsAsync()).Should().NotBeEmpty(
            "a fire against a deleted workflow is an operator-visible event, not a silent drop");
    }

    // ---------------------------------------------------------------- maintenance window

    [Fact]
    public async Task FireAsync_BlockedByMaintenanceWindow_DoesNotDispatch()
    {
        var workflowId = SeedWorkflow(enabled: true);
        _maintenance.Verdict = new MaintenanceEvaluation(
            true, Guid.NewGuid(), "Nightly patching", DateTime.UtcNow.AddHours(2), MaintenanceMode.Blackout);

        await Orchestrator().FireAsync(workflowId, "scheduleTrigger", []);

        _dispatcher.Intents.Should().BeEmpty(
            "an early skip avoids churning out a Cancelled execution every interval");
    }

    [Fact]
    public async Task FireAsync_BlockedByMaintenanceWindow_UsesItsOwnAuditCode()
    {
        var workflowId = SeedWorkflow(enabled: true);
        _maintenance.Verdict = new MaintenanceEvaluation(
            true, Guid.NewGuid(), "Nightly patching", DateTime.UtcNow.AddHours(2), MaintenanceMode.Blackout);

        await Orchestrator().FireAsync(workflowId, "scheduleTrigger", []);

        (await AuditActionsAsync()).Should().Contain(AuditActions.ExecutionBlockedMaintenanceWindow,
            "the timeline must distinguish a maintenance block from a disabled workflow");
    }

    [Fact]
    public async Task FireAsync_MaintenanceWindowNotBlocking_DispatchesNormally()
    {
        var workflowId = SeedWorkflow(enabled: true);
        _maintenance.Verdict = MaintenanceEvaluation.Allowed;

        await Orchestrator().FireAsync(workflowId, "scheduleTrigger", []);

        _dispatcher.Intents.Should().ContainSingle();
    }

    // ---------------------------------------------------------------- leader gating

    [Fact]
    public async Task FireAsync_OnAFollowerNode_DropsTheFireBeforeTouchingTheDatabase()
    {
        var workflowId = SeedWorkflow(enabled: true);

        await Orchestrator(isLeader: false).FireAsync(workflowId, "scheduleTrigger", []);

        _dispatcher.Intents.Should().BeEmpty();
        (await AuditActionsAsync()).Should().BeEmpty(
            "a follower must stay completely silent — no dispatch, no audit noise");
    }

    [Fact]
    public async Task AdmitFireAsync_DatabaseUnavailable_DefersBeforeReadingLeadership()
    {
        var cluster = new ThrowingClusterState();
        var orchestrator = new TriggerOrchestrator(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services,
            cluster,
            NullLogger<TriggerOrchestrator>.Instance,
            NodePilot.TestCommons.TestDatabaseAvailability.Unavailable,
            new TriggerHealthRegistry());

        var admitted = await orchestrator.AdmitFireAsync(
            Guid.NewGuid(), "schedule-node", "scheduleTrigger", "config-v1",
            new TriggerSignal("schedule:outage", "2026-08-27T10:00:00Z", []));

        admitted.Should().BeFalse("the source must retain and retry an outage signal");
        cluster.IsLeaderReads.Should().Be(0);
        _dispatcher.Intents.Should().BeEmpty();
    }

    [Fact]
    public async Task FireAsync_LeaseEpochChangesAfterRead_DoesNotDispatch()
    {
        var workflowId = SeedWorkflow(enabled: true);
        var cluster = new MutableClusterState();
        _maintenance.OnEvaluate = cluster.ReacquireWithNextEpoch;
        var orchestrator = new TriggerOrchestrator(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services,
            cluster,
            NullLogger<TriggerOrchestrator>.Instance,
            NodePilot.TestCommons.TestDatabaseAvailability.Available,
            new TriggerHealthRegistry());

        await orchestrator.FireAsync(workflowId, "scheduleTrigger", []);

        _dispatcher.Intents.Should().BeEmpty(
            "a fire observed under an old lease must not persist or dispatch after a hand-off");
    }

    // ---------------------------------------------------------------- helpers

    private Guid SeedWorkflow(bool enabled)
    {
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "triggered",
            DefinitionJson = "{}",
            IsEnabled = enabled,
        };
        _db.Workflows.Add(workflow);
        _db.SaveChanges();
        return workflow.Id;
    }

    private async Task<List<string>> AuditActionsAsync()
    {
        _db.ChangeTracker.Clear();
        return await _db.AuditLog.AsNoTracking().Select(entry => entry.Action)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private TriggerOrchestrator Orchestrator(bool isLeader = true) => new(
        _services.GetRequiredService<IServiceScopeFactory>(),
        _services,
        isLeader
            ? new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider()
            : new FollowerClusterState(),
        NullLogger<TriggerOrchestrator>.Instance, NodePilot.TestCommons.TestDatabaseAvailability.Available,
        new TriggerHealthRegistry());

    private sealed class RecordingDispatcher : IWorkflowExecutionDispatcher
    {
        public List<WorkflowDispatchIntent> Intents { get; } = [];
        public Exception? Failure { get; set; }

        public Task<WorkflowExecution> DispatchAsync(WorkflowDispatchIntent intent, CancellationToken ct)
        {
            if (Failure is not null) throw Failure;
            Intents.Add(intent);
            return Task.FromResult(new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                WorkflowId = intent.WorkflowId,
                Status = ExecutionStatus.Pending,
                StartedAt = DateTime.UtcNow,
                TriggeredBy = intent.TriggeredBy,
            });
        }
    }

    private sealed class FollowerClusterState : IClusterStateProvider
    {
        public bool IsLeader => false;
        public string NodeId => "follower";
        public DateTime? LeaseExpiresAt => null;
        public long LeaseEpoch => 0;
        public DateTime? LastSuccessfulRenewAt => null;
        public event Action<long>? OnLeadershipAcquired { add { } remove { } }
        public event Action? OnLeadershipLost { add { } remove { } }
    }

    private sealed class ThrowingClusterState : IClusterStateProvider
    {
        public int IsLeaderReads { get; private set; }
        public bool IsLeader
        {
            get
            {
                IsLeaderReads++;
                throw new InvalidOperationException("leadership must not be inspected");
            }
        }
        public string NodeId => "throwing";
        public DateTime? LeaseExpiresAt => null;
        public long LeaseEpoch => 1;
        public DateTime? LastSuccessfulRenewAt => null;
        public event Action<long>? OnLeadershipAcquired { add { } remove { } }
        public event Action? OnLeadershipLost { add { } remove { } }
    }

    private sealed class MutableClusterState : IClusterStateProvider
    {
        public bool IsLeader => true;
        public string NodeId => "mutable";
        public DateTime? LeaseExpiresAt => DateTime.UtcNow.AddMinutes(1);
        public long LeaseEpoch { get; private set; } = 41;
        public DateTime? LastSuccessfulRenewAt => DateTime.UtcNow;
        public event Action<long>? OnLeadershipAcquired;
        public event Action? OnLeadershipLost;

        public void ReacquireWithNextEpoch()
        {
            OnLeadershipLost?.Invoke();
            LeaseEpoch++;
            OnLeadershipAcquired?.Invoke(LeaseEpoch);
        }
    }
}
