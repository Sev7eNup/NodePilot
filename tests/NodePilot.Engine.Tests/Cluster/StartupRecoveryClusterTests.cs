using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Engine.Execution;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Engine.Tests.Cluster;

/// <summary>
/// Cluster-failover semantics for <see cref="StartupRecovery"/>: when called with a
/// <c>ourNodeId</c>, only rows whose <c>OwnerNodeId</c> does not match are recovered.
/// This is what makes a freshly-promoted leader's recovery sweep safe to run
/// repeatedly — it never touches its own in-flight work.
/// </summary>
public sealed class StartupRecoveryClusterTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly Data.NodePilotDbContext _db;

    public StartupRecoveryClusterTests()
    {
        var (conn, db) = TestDbFactory.CreateWithConnection();
        _connection = conn;
        _db = db;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private Workflow SeedWorkflow()
    {
        var wf = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "wf",
            DefinitionJson = "{\"nodes\":[],\"edges\":[]}",
            IsEnabled = true,
            Version = 1
        };
        _db.Workflows.Add(wf);
        _db.SaveChanges();
        return wf;
    }

    private Guid SeedExecution(Guid workflowId, ExecutionStatus status, string? ownerNodeId)
    {
        var id = Guid.NewGuid();
        _db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = id,
            WorkflowId = workflowId,
            Status = status,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            OwnerNodeId = ownerNodeId
        });
        _db.SaveChanges();
        return id;
    }

    private void SetLease(string ownerNodeId, long leaseEpoch)
    {
        var existing = _db.ClusterLeaders.SingleOrDefault(candidate => candidate.Resource == "primary");
        if (existing is null)
        {
            existing = new ClusterLeader { Resource = "primary" };
            _db.ClusterLeaders.Add(existing);
        }
        var now = DateTime.UtcNow;
        existing.OwnerNodeId = ownerNodeId;
        existing.LeaseEpoch = leaseEpoch;
        existing.AcquiredAt = now;
        existing.LastRenewedAt = now;
        existing.ExpiresAt = now.AddMinutes(1);
        _db.SaveChanges();
    }

    [Fact]
    public async Task ClusterMode_RecoversRowsOwnedByOtherNodes_LeavesOurOwnAlone()
    {
        var wf = SeedWorkflow();
        var ours = SeedExecution(wf.Id, ExecutionStatus.Running, ownerNodeId: "node-a");
        var theirs = SeedExecution(wf.Id, ExecutionStatus.Running, ownerNodeId: "node-b");
        SetLease("node-a", 5);

        var recovered = await StartupRecovery.RecoverOrphanedExecutionsAsync(
            _db, NullLogger.Instance, ourNodeId: "node-a", leaseEpoch: 5);

        recovered.Should().Be(1);
        (await _db.WorkflowExecutions.FindAsync(ours))!.Status.Should().Be(ExecutionStatus.Running,
            "our own running row must NOT be cancelled by failover recovery");
        var theirRow = await _db.WorkflowExecutions.FindAsync(theirs);
        theirRow!.Status.Should().Be(ExecutionStatus.Cancelled);
        theirRow.ErrorMessage.Should().Contain("node-a", "audit trail must mention which node recovered");
        theirRow.ErrorMessage.Should().Contain("leaseEpoch=5");
    }

    [Fact]
    public async Task ClusterMode_RecoversLegacyRowsWithNullOwner()
    {
        // Pre-cluster rows with OwnerNodeId=NULL must be recovered the first time too —
        // they are by definition not "ours" and must not stay forever Running.
        var wf = SeedWorkflow();
        var legacy = SeedExecution(wf.Id, ExecutionStatus.Running, ownerNodeId: null);
        SetLease("node-a", 1);

        var recovered = await StartupRecovery.RecoverOrphanedExecutionsAsync(
            _db, NullLogger.Instance, ourNodeId: "node-a", leaseEpoch: 1);

        recovered.Should().Be(1);
        var row = await _db.WorkflowExecutions.FindAsync(legacy);
        row!.Status.Should().Be(ExecutionStatus.Cancelled);
        row.ErrorMessage.Should().Contain("<null>");
    }

    [Fact]
    public async Task ClusterMode_RecoversPendingAndPaused()
    {
        var wf = SeedWorkflow();
        var pending = SeedExecution(wf.Id, ExecutionStatus.Pending, ownerNodeId: "dead-leader");
        var paused = SeedExecution(wf.Id, ExecutionStatus.Paused, ownerNodeId: "dead-leader");
        SetLease("node-a", 2);

        var recovered = await StartupRecovery.RecoverOrphanedExecutionsAsync(
            _db, NullLogger.Instance, ourNodeId: "node-a", leaseEpoch: 2);

        recovered.Should().Be(2);
        (await _db.WorkflowExecutions.FindAsync(pending))!.Status.Should().Be(ExecutionStatus.Cancelled);
        (await _db.WorkflowExecutions.FindAsync(paused))!.Status.Should().Be(ExecutionStatus.Cancelled);
    }

    [Fact]
    public async Task ClusterMode_TerminalRowsAreNotTouched()
    {
        var wf = SeedWorkflow();
        var done = SeedExecution(wf.Id, ExecutionStatus.Succeeded, ownerNodeId: "node-b");
        var failed = SeedExecution(wf.Id, ExecutionStatus.Failed, ownerNodeId: "node-b");
        SetLease("node-a", 7);

        var recovered = await StartupRecovery.RecoverOrphanedExecutionsAsync(
            _db, NullLogger.Instance, ourNodeId: "node-a", leaseEpoch: 7);

        recovered.Should().Be(0);
        (await _db.WorkflowExecutions.FindAsync(done))!.Status.Should().Be(ExecutionStatus.Succeeded);
        (await _db.WorkflowExecutions.FindAsync(failed))!.Status.Should().Be(ExecutionStatus.Failed);
    }

    [Fact]
    public async Task ClusterMode_DelayedOldEpochRecovery_CannotCancelNewLeaderRows()
    {
        var wf = SeedWorkflow();
        var newLeaderExecution = SeedExecution(
            wf.Id, ExecutionStatus.Running, ownerNodeId: "node-b");
        var oldOrphan = SeedExecution(
            wf.Id, ExecutionStatus.Running, ownerNodeId: "node-c");
        SetLease("node-b", 8);

        var recovered = await StartupRecovery.RecoverOrphanedExecutionsAsync(
            _db, NullLogger.Instance, ourNodeId: "node-a", leaseEpoch: 7);

        recovered.Should().Be(0);
        (await _db.WorkflowExecutions.FindAsync(newLeaderExecution))!.Status
            .Should().Be(ExecutionStatus.Running);
        (await _db.WorkflowExecutions.FindAsync(oldOrphan))!.Status
            .Should().Be(ExecutionStatus.Running,
                "only the current leader may perform orphan recovery");
    }
}
