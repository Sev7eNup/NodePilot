using System.Data.Common;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Api.Hosting;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Availability;
using NodePilot.Engine.Execution;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

public sealed class ClusterFailoverRecoveryHostTests
{
    [Fact]
    public async Task DeferredSingleExecution_RetriesWithFreshScopeInSameEpoch()
    {
        var (connection, seed) = NodePilot.TestCommons.TestDbFactory.CreateWithConnection();
        await using var connectionLifetime = connection;
        await using var seedLifetime = seed;
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "recovery" };
        seed.Workflows.Add(workflow);
        seed.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, Status = ExecutionStatus.Running,
            OwnerNodeId = "old", StartedAt = DateTime.UtcNow,
        });
        seed.ClusterLeaders.Add(new ClusterLeader
        {
            Resource = "primary", OwnerNodeId = "new", LeaseEpoch = 7,
            ExpiresAt = DateTime.UtcNow.AddMinutes(1), LastRenewedAt = DateTime.UtcNow,
        });
        await seed.SaveChangesAsync(TestContext.Current.CancellationToken);

        var contexts = new List<ObservedContext>();
        var timedOut = new TimedOutSave();
        var committed = new CommittedBatch();
        var registrations = new ServiceCollection();
        registrations.AddScoped<NodePilotDbContext>(_ =>
        {
            if (contexts.Count > 0) contexts[^1].Disposed.Should().BeTrue();
            var options = new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(connection);
            if (contexts.Count == 0) options.AddInterceptors(timedOut);
            else options.AddInterceptors(committed);
            var context = new ObservedContext(options.Options);
            contexts.Add(context);
            return context;
        });
        await using var services = registrations.BuildServiceProvider();
        var availability = new Mock<IDatabaseAvailability>();
        availability.Setup(a => a.WaitUntilServableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var cluster = new ClusterState();
        // The retry runs under the default batch budget; a shorter one also bounds the retry and
        // fails on a loaded runner.
        var logger = new NodePilot.TestCommons.CapturingLogger<ClusterFailoverRecoveryHost>();
        using var host = new ClusterFailoverRecoveryHost(services.GetRequiredService<IServiceScopeFactory>(),
            cluster, logger, availability.Object, new ConfigurationBuilder().Build());
        try
        {
            cluster.Acquire(7);
            await committed.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally { await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); }

        contexts.Should().HaveCount(2);
        logger.Entries.Should().ContainSingle(e => e.Exception is ClusterRecoveryDeferredException,
            "the first attempt must take the deferral path, not the generic failure retry");
        logger.Entries.Should().NotContain(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error);
        (await seed.WorkflowExecutions.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).Status
            .Should().Be(ExecutionStatus.Cancelled);
        (await seed.AuditLog.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task LeadershipLoss_CancelsRecoveryParkedAtAvailabilityGate()
    {
        var cluster = new ClusterState();
        var arrivals = Channel.CreateUnbounded<CancellationToken>();
        var availability = ParkedAvailability(arrivals);
        await using var services = new ServiceCollection().BuildServiceProvider();
        using var host = CreateHost(services, cluster, availability.Object);

        cluster.Acquire(7);
        var token = await NextAsync(arrivals);
        cluster.Lose();
        token.IsCancellationRequested.Should().BeTrue();
        await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        availability.Verify(a => a.WaitUntilServableAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NewEpoch_WaitsForOldAttemptToUnwind_AndDuplicateEventDoesNotStartAnother()
    {
        var cluster = new ClusterState();
        var arrivals = Channel.CreateUnbounded<CancellationToken>();
        var releaseOldAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var availability = new Mock<IDatabaseAvailability>();
        availability.Setup(a => a.WaitUntilServableAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken token) =>
            {
                var first = Interlocked.Increment(ref calls) == 1;
                arrivals.Writer.TryWrite(token);
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { if (first) await releaseOldAttempt.Task; }
                return false;
            });
        await using var services = new ServiceCollection().BuildServiceProvider();
        using var host = CreateHost(services, cluster, availability.Object);
        try
        {
            cluster.Acquire(7);
            var oldToken = await NextAsync(arrivals);
            cluster.Acquire(8);
            cluster.Acquire(8);
            oldToken.IsCancellationRequested.Should().BeTrue();
            await Task.Delay(100);
            calls.Should().Be(1, "the old attempt must release its scope before the new epoch starts");

            releaseOldAttempt.TrySetResult();
            var newToken = await NextAsync(arrivals);
            newToken.IsCancellationRequested.Should().BeFalse();
            cluster.Acquire(8);
            await Task.Delay(100);
            calls.Should().Be(2);
        }
        finally
        {
            releaseOldAttempt.TrySetResult();
            await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Shutdown_CancelsInFlightDatabaseRead_DisposesScope_AndUnsubscribes()
    {
        var cluster = new ClusterState();
        var read = new ParkedRead();
        var availability = new Mock<IDatabaseAvailability>();
        availability.Setup(a => a.WaitUntilServableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var registrations = new ServiceCollection();
        registrations.AddScoped<NodePilotDbContext>(_ => new ObservedContext(
            new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite("Data Source=:memory:")
                .AddInterceptors(read).Options));
        await using var services = registrations.BuildServiceProvider();
        using var host = CreateHost(services, cluster, availability.Object);

        cluster.Acquire(7);
        var context = await read.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        read.Token.IsCancellationRequested.Should().BeTrue();
        context.Disposed.Should().BeTrue();
        cluster.Acquire(8);
        availability.Verify(a => a.WaitUntilServableAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private static ClusterFailoverRecoveryHost CreateHost(
        IServiceProvider services, IClusterStateProvider cluster, IDatabaseAvailability availability)
        => new(services.GetRequiredService<IServiceScopeFactory>(), cluster,
            NullLogger<ClusterFailoverRecoveryHost>.Instance, availability,
            new ConfigurationBuilder().Build());

    private static Mock<IDatabaseAvailability> ParkedAvailability(Channel<CancellationToken> arrivals)
    {
        var availability = new Mock<IDatabaseAvailability>();
        availability.Setup(a => a.WaitUntilServableAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken token) =>
            {
                arrivals.Writer.TryWrite(token);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return false;
            });
        return availability;
    }

    private static async Task<CancellationToken> NextAsync(Channel<CancellationToken> arrivals)
        => await arrivals.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    private sealed class ParkedRead : DbCommandInterceptor
    {
        public TaskCompletionSource<ObservedContext> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            Entered.TrySetResult((ObservedContext)eventData.Context!);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return result;
        }
    }

    private sealed class ObservedContext(DbContextOptions<NodePilotDbContext> options) : NodePilotDbContext(options)
    {
        public bool Disposed { get; private set; }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            Disposed = true;
        }
    }

    // A command timeout defers the batch exactly like an expired budget; the budget path itself
    // is covered by StartupRecoveryBatchTests.
    private sealed class TimedOutSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => throw new TimeoutException("Simulated command timeout.");
    }

    private sealed class CommittedBatch : DbTransactionInterceptor
    {
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task TransactionCommittedAsync(DbTransaction transaction,
            TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Completed.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class ClusterState : IClusterStateProvider
    {
        public bool IsLeader { get; private set; }
        public string NodeId => "new";
        public DateTime? LeaseExpiresAt => DateTime.UtcNow.AddSeconds(30);
        public long LeaseEpoch { get; private set; }
        public DateTime? LastSuccessfulRenewAt => DateTime.UtcNow;
        public event Action<long>? OnLeadershipAcquired;
        public event Action? OnLeadershipLost;
        public void Acquire(long epoch)
        {
            IsLeader = true;
            LeaseEpoch = epoch;
            OnLeadershipAcquired?.Invoke(epoch);
        }
        public void Lose()
        {
            IsLeader = false;
            OnLeadershipLost?.Invoke();
        }
    }
}
