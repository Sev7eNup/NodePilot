using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Execution;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Engine.Tests.Cluster;

public sealed class StartupRecoveryBatchTests
{
    [Fact]
    public async Task LargeRecovery_CommitsExecutionAndAuditInBoundedBatches()
    {
        var (connection, seed) = TestDbFactory.CreateWithConnection();
        await using var connectionLifetime = connection;
        await using var seedLifetime = seed;
        await SeedAsync(seed, 251);
        var transactions = new RecoveryTransactions();
        await using var db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connection).AddInterceptors(transactions).Options);

        var count = await StartupRecovery.RecoverOrphanedExecutionsAsync(
            db, NullLogger.Instance, ourNodeId: "new", leaseEpoch: 7);

        count.Should().Be(251);
        transactions.AuditCounts.Should().HaveCount(3);
        transactions.AuditCounts.Should().OnlyContain(count => count <= 100);
        (await seed.AuditLog.CountAsync(a => a.Action == AuditActions.ExecutionRecoveredFailover))
            .Should().Be(251);
        (await seed.StepExecutions.CountAsync(s => s.Status == ExecutionStatus.Cancelled)).Should().Be(251);
    }

    [Fact]
    public async Task EpochChangesBetweenBatches_PreservesCommittedProgressAndStops()
    {
        var (connection, seed) = TestDbFactory.CreateWithConnection();
        await using var connectionLifetime = connection;
        await using var seedLifetime = seed;
        await SeedAsync(seed, 201);
        var transactions = new RecoveryTransactions(async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE ClusterLeaders SET LeaseEpoch = 8, OwnerNodeId = 'successor'";
            await command.ExecuteNonQueryAsync();
        });
        await using var db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connection).AddInterceptors(transactions).Options);

        var count = await StartupRecovery.RecoverOrphanedExecutionsAsync(
            db, NullLogger.Instance, ourNodeId: "new", leaseEpoch: 7);

        count.Should().Be(100);
        (await seed.WorkflowExecutions.CountAsync(e => e.Status == ExecutionStatus.Running)).Should().Be(101);
        (await seed.AuditLog.CountAsync(a => a.Action == AuditActions.ExecutionRecoveredFailover)).Should().Be(100);
    }

    [Fact]
    public async Task LostCommitAcknowledgement_VerifiesStableAuditIdsWithoutReplay()
    {
        var (connection, seed) = TestDbFactory.CreateWithConnection();
        await using var connectionLifetime = connection;
        await using var seedLifetime = seed;
        await SeedAsync(seed, 3);
        var transactions = new RecoveryTransactions(() => throw new TimeoutException("Lost commit acknowledgement"));
        await using var db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connection).AddInterceptors(transactions).Options);

        var count = await StartupRecovery.RecoverOrphanedExecutionsAsync(
            db, NullLogger.Instance, ourNodeId: "new", leaseEpoch: 7);

        count.Should().Be(3);
        transactions.AuditCounts.Should().ContainSingle().Which.Should().Be(3);
        (await seed.AuditLog.CountAsync(a => a.Action == AuditActions.ExecutionRecoveredFailover)).Should().Be(3);
    }

    [Fact]
    public async Task LostCommitAndFirstVerificationTimeout_RetainsResultUntilReadSucceeds()
    {
        var (connection, seed) = TestDbFactory.CreateWithConnection();
        await using var connectionLifetime = connection;
        await using var seedLifetime = seed;
        await SeedAsync(seed, 3);
        var transactions = new RecoveryTransactions(() => throw new TimeoutException("Lost commit acknowledgement"));
        var verification = new StallFirstVerification();
        await using var db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connection).AddInterceptors(transactions, verification).Options);

        var count = await StartupRecovery.RecoverOrphanedExecutionsAsync(
            db, NullLogger.Instance, ourNodeId: "new", leaseEpoch: 7);

        count.Should().Be(3);
        verification.Reads.Should().Be(2);
        transactions.AuditCounts.Should().ContainSingle().Which.Should().Be(3);
        (await seed.AuditLog.CountAsync(a => a.Action == AuditActions.ExecutionRecoveredFailover)).Should().Be(3);
    }

    [Fact]
    public async Task BudgetExpiry_RollsBackAndRetriesSmallerBatch()
    {
        var (connection, seed) = TestDbFactory.CreateWithConnection();
        await using var connectionLifetime = connection;
        await using var seedLifetime = seed;
        await SeedAsync(seed, 2);
        var stall = new StallAuditSave(stallSingle: false);
        await using var db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connection).AddInterceptors(stall).Options);

        var count = await StartupRecovery.RecoverOrphanedExecutionsAsync(
            db, NullLogger.Instance, ourNodeId: "new", leaseEpoch: 7,
            clusterBatchTimeout: TimeSpan.FromMilliseconds(200));

        count.Should().Be(2);
        stall.BatchSizes.Should().Equal(2, 1, 1);
        (await seed.AuditLog.CountAsync(a => a.Action == AuditActions.ExecutionRecoveredFailover)).Should().Be(2);
    }

    [Fact]
    public async Task SingleExecutionExceedsBudget_DefersWithAllChangesRolledBack()
    {
        var (connection, seed) = TestDbFactory.CreateWithConnection();
        await using var connectionLifetime = connection;
        await using var seedLifetime = seed;
        await SeedAsync(seed, 1);
        await using var db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connection).AddInterceptors(new StallAuditSave(stallSingle: true)).Options);

        var recovery = () => StartupRecovery.RecoverOrphanedExecutionsAsync(
            db, NullLogger.Instance, ourNodeId: "new", leaseEpoch: 7,
            clusterBatchTimeout: TimeSpan.FromMilliseconds(200));

        (await recovery.Should().ThrowAsync<ClusterRecoveryDeferredException>())
            .Which.CompletedExecutions.Should().Be(0);
        (await seed.WorkflowExecutions.SingleAsync()).Status.Should().Be(ExecutionStatus.Running);
        (await seed.StepExecutions.SingleAsync()).Status.Should().Be(ExecutionStatus.Running);
        (await seed.AuditLog.CountAsync(a => a.Action == AuditActions.ExecutionRecoveredFailover)).Should().Be(0);
        db.Database.CurrentTransaction.Should().BeNull();
    }

    [Fact]
    public async Task CancellationDuringAuditSave_RollsBackWithoutDeferral()
    {
        var (connection, seed) = TestDbFactory.CreateWithConnection();
        await using var connectionLifetime = connection;
        await using var seedLifetime = seed;
        await SeedAsync(seed, 1);
        var stall = new StallAuditSave(stallSingle: true);
        await using var db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connection).AddInterceptors(stall).Options);
        using var cancellation = new CancellationTokenSource();
        var recovery = StartupRecovery.RecoverOrphanedExecutionsAsync(
            db, NullLogger.Instance, cancellation.Token, "new", 7);
        await stall.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        Func<Task> awaitRecovery = async () => await recovery;
        await awaitRecovery.Should().ThrowAsync<OperationCanceledException>();
        (await seed.WorkflowExecutions.SingleAsync()).Status.Should().Be(ExecutionStatus.Running);
        (await seed.AuditLog.CountAsync(a => a.Action == AuditActions.ExecutionRecoveredFailover)).Should().Be(0);
        db.Database.CurrentTransaction.Should().BeNull();
    }

    [Fact]
    public async Task DurablePendingLostCommitAcknowledgement_VerifiesOwnership()
    {
        var (connection, seed) = TestDbFactory.CreateWithConnection();
        await using var connectionLifetime = connection;
        await using var seedLifetime = seed;
        await SeedAsync(seed, 1);
        var execution = await seed.WorkflowExecutions.SingleAsync();
        execution.Status = ExecutionStatus.Pending;
        seed.ExecutionDispatchOutbox.Add(new ExecutionDispatchOutboxItem
        {
            ExecutionId = execution.Id, WorkflowId = execution.WorkflowId,
            TriggeredBy = "manual", MissingWorkflowMessage = "missing", PreOwnershipFailurePrefix = "failed",
            LeaseOwner = "old-worker", LeaseExpiresAt = DateTime.UtcNow.AddMinutes(1)
        });
        await seed.SaveChangesAsync();
        seed.ChangeTracker.Clear();
        var transactions = new RecoveryTransactions(() => throw new TimeoutException("Lost adoption acknowledgement"));
        await using var db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connection).AddInterceptors(transactions).Options);

        (await StartupRecovery.RecoverOrphanedExecutionsAsync(
            db, NullLogger.Instance, ourNodeId: "new", leaseEpoch: 7)).Should().Be(0);

        (await seed.WorkflowExecutions.SingleAsync()).OwnerNodeId.Should().Be("new");
        (await seed.ExecutionDispatchOutbox.SingleAsync()).LeaseOwner.Should().BeNull();
        transactions.AuditCounts.Should().ContainSingle().Which.Should().Be(0);
    }

    internal static async Task SeedAsync(NodePilotDbContext db, int count)
    {
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "Recovery", DefinitionJson = "{}" };
        db.Workflows.Add(workflow);
        var lease = await db.ClusterLeaders.SingleOrDefaultAsync(l => l.Resource == "primary");
        if (lease is null)
        {
            lease = new ClusterLeader { Resource = "primary" };
            db.ClusterLeaders.Add(lease);
        }
        lease.OwnerNodeId = "new";
        lease.LeaseEpoch = 7;
        lease.ExpiresAt = DateTime.UtcNow.AddMinutes(5);
        lease.AcquiredAt = lease.LastRenewedAt = DateTime.UtcNow;
        for (var index = 0; index < count; index++)
        {
            var execution = new WorkflowExecution
            {
                Id = Guid.NewGuid(), WorkflowId = workflow.Id, OwnerNodeId = "old",
                Status = ExecutionStatus.Running, StartedAt = DateTime.UtcNow.AddMinutes(-5)
            };
            db.WorkflowExecutions.Add(execution);
            db.StepExecutions.Add(new StepExecution
            {
                Id = Guid.NewGuid(), WorkflowExecutionId = execution.Id, StepId = "step",
                Status = ExecutionStatus.Running, StartedAt = execution.StartedAt
            });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private sealed class RecoveryTransactions(Func<Task>? afterFirstCommit = null) : DbTransactionInterceptor
    {
        public List<int> AuditCounts { get; } = [];

        public override async Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            AuditCounts.Add(eventData.Context!.ChangeTracker.Entries<AuditLogEntry>().Count());
            if (AuditCounts.Count == 1 && afterFirstCommit is not null) await afterFirstCommit();
        }
    }

    private sealed class StallAuditSave(bool stallSingle) : SaveChangesInterceptor
    {
        public List<int> BatchSizes { get; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var size = eventData.Context!.ChangeTracker.Entries<AuditLogEntry>().Count();
            BatchSizes.Add(size);
            if (stallSingle || size > 1)
            {
                Entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return result;
        }
    }

    private sealed class StallFirstVerification : DbCommandInterceptor
    {
        public int Reads { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"AuditLog\"", StringComparison.Ordinal)
                && command.CommandText.Contains("COUNT(*)", StringComparison.Ordinal))
            {
                eventData.Context!.Database.CurrentTransaction.Should().BeNull(
                    "commit verification must not keep the lease locked");
                if (++Reads == 1) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return result;
        }
    }
}
