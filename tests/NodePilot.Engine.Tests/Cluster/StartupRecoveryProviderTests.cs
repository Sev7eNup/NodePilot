using System.Data.Common;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Engine.Execution;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Engine.Tests.Cluster;

[Trait("Category", "DatabaseIntegration")]
public sealed class StartupRecoveryProviderTests
{
    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public async Task SlowMultiBatchRecovery_KeepsConcurrentRenewalWithinItsTimeout(string provider)
    {
        Assert.SkipUnless(ProviderTestDatabase.IsConfigured(provider), $"No {provider} test server configured.");
        await using var database = await ProviderTestDatabase.CreateAsync(provider);
        await using var seed = database.CreateContext();
        await StartupRecoveryBatchTests.SeedAsync(seed, 401);
        var slowSave = new SlowAuditBatches();
        var commits = new CommitCounter();
        await using var db = database.CreateContext(slowSave, commits);
        await using var renewDb = database.CreateContext();
        renewDb.Database.SetCommandTimeout(3);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var elapsed = Stopwatch.StartNew();
        var recovery = StartupRecovery.RecoverOrphanedExecutionsAsync(
            db, NullLogger.Instance, timeout.Token, "new", 7);
        var renewals = 0;
        async Task RenewWhileRecoveringAsync()
        {
            await slowSave.Entered.Task.WaitAsync(timeout.Token);
            while (!recovery.IsCompleted)
            {
                var rows = await renewDb.ClusterLeaders.Where(l => l.Resource == "primary"
                    && l.OwnerNodeId == "new" && l.LeaseEpoch == 7)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresAt, DateTime.UtcNow.AddMinutes(5)), timeout.Token);
                rows.Should().Be(1);
                renewals++;
                await Task.Delay(50, timeout.Token);
            }
        }

        await Task.WhenAll(recovery, RenewWhileRecoveringAsync());

        (await recovery).Should().Be(401);
        elapsed.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(3));
        commits.Count.Should().Be(5);
        renewals.Should().BeGreaterThanOrEqualTo(5,
            "the lease renewal SQL must complete between slow batches without its 3-second timeout");
        (await seed.AuditLog.CountAsync(a => a.Action == AuditActions.ExecutionRecoveredFailover)).Should().Be(401);
        (await seed.ClusterLeaders.SingleAsync(l => l.Resource == "primary")).LeaseEpoch.Should().Be(7);
    }

    [Theory]
    [InlineData("postgres", false)]
    [InlineData("sqlserver", false)]
    [InlineData("sqlserver", true)]
    public async Task RenewalAndEpochChangeBetweenBatches_CommitOnlyAuthorizedBatch(string provider, bool rcsi)
    {
        Assert.SkipUnless(ProviderTestDatabase.IsConfigured(provider), $"No {provider} test server configured.");
        await using var database = await ProviderTestDatabase.CreateAsync(provider, rcsi);
        await using var seed = database.CreateContext();
        await StartupRecoveryBatchTests.SeedAsync(seed, 201);
        var barrier = new FirstCommitBarrier();
        await using var db = database.CreateContext(barrier);
        await using var renewDb = database.CreateContext();
        renewDb.Database.SetCommandTimeout(3);
        var recovery = StartupRecovery.RecoverOrphanedExecutionsAsync(
            db, NullLogger.Instance, ourNodeId: "new", leaseEpoch: 7);
        try
        {
            await barrier.Committed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var renewed = await renewDb.ClusterLeaders.Where(l => l.Resource == "primary"
                && l.OwnerNodeId == "new" && l.LeaseEpoch == 7)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresAt, DateTime.UtcNow.AddMinutes(5)));
            renewed.Should().Be(1, "the previous recovery batch must have released the lease lock");
            await renewDb.ClusterLeaders.Where(l => l.Resource == "primary")
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.OwnerNodeId, "successor")
                    .SetProperty(l => l.LeaseEpoch, 8));
        }
        finally { barrier.Continue.TrySetResult(); }

        (await recovery).Should().Be(100);
        (await seed.WorkflowExecutions.CountAsync(e => e.Status == ExecutionStatus.Running)).Should().Be(101);
        (await seed.AuditLog.CountAsync(a => a.Action == AuditActions.ExecutionRecoveredFailover)).Should().Be(100);
    }

    [Theory]
    [InlineData("postgres", false)]
    [InlineData("sqlserver", false)]
    [InlineData("sqlserver", true)]
    public async Task SlowBatch_RollsBackBeforeWaitingRenewalTimesOut(string provider, bool rcsi)
    {
        Assert.SkipUnless(ProviderTestDatabase.IsConfigured(provider), $"No {provider} test server configured.");
        await using var database = await ProviderTestDatabase.CreateAsync(provider, rcsi);
        await using var seed = database.CreateContext();
        await StartupRecoveryBatchTests.SeedAsync(seed, 1);
        var stall = new StallAuditSave();
        await using var db = database.CreateContext(stall);
        await using var renewDb = database.CreateContext();
        renewDb.Database.SetCommandTimeout(3);
        var recovery = StartupRecovery.RecoverOrphanedExecutionsAsync(
            db, NullLogger.Instance, ourNodeId: "new", leaseEpoch: 7,
            clusterBatchTimeout: TimeSpan.FromMilliseconds(750));
        await stall.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var renew = renewDb.ClusterLeaders.Where(l => l.Resource == "primary" && l.OwnerNodeId == "new")
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresAt, DateTime.UtcNow.AddMinutes(5)));

        Func<Task> awaitRecovery = async () => await recovery;
        await awaitRecovery.Should().ThrowAsync<ClusterRecoveryDeferredException>();
        (await renew).Should().Be(1);
        (await seed.WorkflowExecutions.SingleAsync()).Status.Should().Be(ExecutionStatus.Running);
        (await seed.StepExecutions.SingleAsync()).Status.Should().Be(ExecutionStatus.Running);
        (await seed.AuditLog.CountAsync(a => a.Action == AuditActions.ExecutionRecoveredFailover)).Should().Be(0);
    }

    [Theory]
    [InlineData("postgres", false)]
    [InlineData("sqlserver", false)]
    public async Task LostCommitAcknowledgement_PreservesCountsAndOneAuditPerExecution(string provider, bool rcsi)
    {
        Assert.SkipUnless(ProviderTestDatabase.IsConfigured(provider), $"No {provider} test server configured.");
        await using var database = await ProviderTestDatabase.CreateAsync(provider, rcsi);
        await using var seed = database.CreateContext();
        await StartupRecoveryBatchTests.SeedAsync(seed, 3);
        await using var db = database.CreateContext(new LoseCommitAcknowledgement());

        (await StartupRecovery.RecoverOrphanedExecutionsAsync(
            db, NullLogger.Instance, ourNodeId: "new", leaseEpoch: 7)).Should().Be(3);

        (await seed.AuditLog.CountAsync(a => a.Action == AuditActions.ExecutionRecoveredFailover)).Should().Be(3);
    }

    private sealed class FirstCommitBarrier : DbTransactionInterceptor
    {
        private int _commits;
        public TaskCompletionSource Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _commits) != 1) return;
            Committed.TrySetResult();
            await Continue.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class StallAuditSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AuditLogEntry>().Any())
            {
                Entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return result;
        }
    }

    private sealed class LoseCommitAcknowledgement : DbTransactionInterceptor
    {
        private int _fired;
        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _fired, 1) == 0) throw new IOException("Lost commit acknowledgement");
            return Task.CompletedTask;
        }
    }

    private sealed class SlowAuditBatches : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var count = eventData.Context!.ChangeTracker.Entries<AuditLogEntry>().Count();
            if (count > 0)
            {
                Entered.TrySetResult();
                await Task.Delay(TimeSpan.FromMilliseconds(count * 8), cancellationToken);
            }
            return result;
        }
    }

    private sealed class CommitCounter : DbTransactionInterceptor
    {
        public int Count { get; private set; }
        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Count++;
            return Task.CompletedTask;
        }
    }
}
