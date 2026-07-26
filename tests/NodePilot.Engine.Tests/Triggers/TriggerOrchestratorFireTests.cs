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
using NodePilot.Scheduler;
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
    private readonly StubMaintenanceEvaluator _maintenance = new();

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
        NullLogger<TriggerOrchestrator>.Instance);

    private sealed class RecordingDispatcher : IWorkflowExecutionDispatcher
    {
        public List<WorkflowDispatchIntent> Intents { get; } = [];

        public Task<WorkflowExecution> DispatchAsync(WorkflowDispatchIntent intent, CancellationToken ct)
        {
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

    private sealed class StubMaintenanceEvaluator : IMaintenanceWindowEvaluator
    {
        public MaintenanceEvaluation Verdict { get; set; } = MaintenanceEvaluation.Allowed;

        public MaintenanceEvaluation Evaluate(Guid workflowId, Guid folderId, DateTime nowUtc) => Verdict;

        public IReadOnlyList<MaintenanceWindowSummary> GetWindowsAffecting(
            Guid workflowId, Guid folderId, DateTime nowUtc) => [];

        public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
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
}
