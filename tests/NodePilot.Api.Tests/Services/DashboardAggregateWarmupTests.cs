using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Api.Services;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Availability;
using Xunit;

namespace NodePilot.Api.Tests.Services;

/// <summary>
/// Covers <see cref="DashboardAggregateWarmup"/>. It exists so the person who opens the dashboard
/// first finds the expensive window aggregates already computed, instead of paying for them.
/// </summary>
public sealed class DashboardAggregateWarmupTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly NodePilotDbContext _db;

    public DashboardAggregateWarmupTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        _db = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _provider.Dispose();
        _connection.Dispose();
    }

    private (DashboardAggregateWarmup warmup, DashboardAggregateCache cache) Build(IServiceScopeFactory? scopes = null)
    {
        scopes ??= _provider.GetRequiredService<IServiceScopeFactory>();
        var cache = new DashboardAggregateCache(scopes);
        var availability = new Mock<IDatabaseAvailability>();
        availability.Setup(a => a.WaitUntilServableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var warmup = new DashboardAggregateWarmup(
            cache,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            availability.Object,
            new NodePilot.Engine.Security.OutputRedactor(config),
            config,
            NullLogger<DashboardAggregateWarmup>.Instance);
        return (warmup, cache);
    }

    [Fact]
    public async Task PrimeAsync_FillsTheCommonWindows()
    {
        var workflowId = Guid.NewGuid();
        _db.Workflows.Add(new Workflow
        {
            Id = workflowId, Name = "W", DefinitionJson = "{}", UpdatedAt = DateTime.UtcNow,
        });
        _db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflowId,
            Status = NodePilot.Core.Enums.ExecutionStatus.Failed,
            StartedAt = DateTime.UtcNow,
            ErrorMessage = "boom",
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (warmup, cache) = Build();
        await warmup.PrimeAsync(TestContext.Current.CancellationToken);

        // Three windows, three aggregates each.
        cache.Count.Should().Be(9);
    }

    [Fact]
    public async Task PrimeAsync_ThenAReaderHits_WithoutRecomputing()
    {
        var (warmup, cache) = Build();
        await warmup.PrimeAsync(TestContext.Current.CancellationToken);

        var computed = false;
        var key = DashboardAggregateCache.Key("window", AccessibleFolderSet.Unrestricted, 720);
        var result = await cache.GetOrComputeAsync(
            key,
            TimeSpan.FromMinutes(5),
            (_, _) => { computed = true; return Task.FromResult<DashboardWindowAggregates>(null!); },
            TestContext.Current.CancellationToken);

        computed.Should().BeFalse("the primed value must satisfy the first reader");
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task PrimeAsync_PrimedEntriesStayEligibleForRefresh()
    {
        // Regression guard for the bug that made the warm-up pointless: primed entries were skipped
        // by the refresh sweep, expired one TTL after startup and were then dropped. Keeping them
        // renewable is affordable now that the aggregates come from precomputed buckets.
        var (warmup, cache) = Build();
        await warmup.PrimeAsync(TestContext.Current.CancellationToken);

        var refreshed = await cache.RefreshDueAsync(
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(4), TestContext.Current.CancellationToken);

        refreshed.Should().Be(cache.Count, "every primed window must be renewable");
        refreshed.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task PrimeAsync_DurationTrendEntry_IsServedToTheController()
    {
        var (warmup, cache) = Build();
        await warmup.PrimeAsync(TestContext.Current.CancellationToken);

        var computed = false;
        await cache.GetOrComputeAsync(
            DashboardAggregateCache.Key("duration-trend:all", AccessibleFolderSet.Unrestricted, 720),
            DashboardCacheSettings.Ttl,
            (_, _) => { computed = true; return Task.FromResult<NodePilot.Api.Dtos.DurationTrendResponse>(null!); },
            TestContext.Current.CancellationToken);

        computed.Should().BeFalse();
    }

    private async Task CoverWithBucketsAsync()
    {
        var hour = ExecutionStatsRollupService.Truncate(DateTime.UtcNow);
        _db.ExecutionStatsRollupStates.Add(new ExecutionStatsRollupState
        {
            Id = ExecutionStatsRollupService.StateRowId,
            CoverageStartUtc = hour,
            CoverageEndUtc = hour,
            BackfillComplete = true,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SweepAsync_AfterTheCacheWasCleared_PrimesTheCommonWindowsAgain()
    {
        // A folder mutation clears the cache; without priming again, the admin's 30-day view would
        // stay cold until the next restart.
        await CoverWithBucketsAsync();
        var (warmup, cache) = Build();
        await warmup.PrimeAsync(TestContext.Current.CancellationToken);
        cache.Clear();
        cache.Count.Should().Be(0);

        await warmup.SweepAsync(TestContext.Current.CancellationToken);

        cache.Count.Should().Be(9);
    }

    /// <summary>Fails the first read of the rollup state, i.e. the first primed window.</summary>
    private sealed class FailFirstRollupStateRead : DbCommandInterceptor
    {
        private int _failed;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"ExecutionStatsRollupStates\"", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _failed, 1) == 0)
                throw new InvalidOperationException("simulated failure");
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task SweepAsync_AfterAnIncompletePriming_PrimesAgain()
    {
        await CoverWithBucketsAsync();
        var failFirst = new FailFirstRollupStateRead();
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(o => o.UseSqlite(_connection).AddInterceptors(failFirst));
        await using var failingOnce = services.BuildServiceProvider();
        var (warmup, cache) = Build(failingOnce.GetRequiredService<IServiceScopeFactory>());
        await warmup.PrimeAsync(TestContext.Current.CancellationToken);

        await warmup.SweepAsync(TestContext.Current.CancellationToken);

        var recomputed = false;
        await cache.GetOrComputeAsync(
            DashboardAggregateCache.Key("window", AccessibleFolderSet.Unrestricted, 24),
            DashboardCacheSettings.Ttl,
            (_, _) => { recomputed = true; return Task.FromResult<DashboardWindowAggregates>(null!); },
            TestContext.Current.CancellationToken);
        recomputed.Should().BeFalse("the window that failed during priming was primed again by the sweep");
        cache.Count.Should().Be(9);
    }

    [Fact]
    public async Task SweepAsync_AfterClearWithoutBucketCoverage_DoesNotRecomputeFromRawRows()
    {
        var (warmup, cache) = Build();
        await warmup.PrimeAsync(TestContext.Current.CancellationToken);
        cache.Clear();

        await warmup.SweepAsync(TestContext.Current.CancellationToken);

        cache.Count.Should().Be(0, "without buckets every entry would be a raw-row aggregation nobody asked for");
    }

    [Fact]
    public async Task RefreshDueAsync_PinnedEntriesRefreshWithoutRecentRequest_OthersDoNot()
    {
        var (warmup, cache) = Build();
        await warmup.PrimeAsync(TestContext.Current.CancellationToken);
        // A scoped caller's entry is never pinned.
        await cache.GetOrComputeAsync(
            DashboardAggregateCache.Key("window", new AccessibleFolderSet { FolderIds = [Guid.NewGuid()] }, 720),
            TimeSpan.FromSeconds(1),
            (_, _) => Task.FromResult(0),
            TestContext.Current.CancellationToken);

        // Nobody asked for anything within the active window.
        var refreshed = await cache.RefreshDueAsync(
            TimeSpan.FromHours(1), TimeSpan.Zero, TestContext.Current.CancellationToken, includePinned: true);

        // Window, failure causes and duration trend for three windows; the scoped entry is not refreshed.
        refreshed.Should().Be(9);
    }
}
