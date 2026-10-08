using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

    private (DashboardAggregateWarmup warmup, DashboardAggregateCache cache) Build()
    {
        var cache = new DashboardAggregateCache(_provider.GetRequiredService<IServiceScopeFactory>());
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

    [Fact]
    public async Task RefreshDueAsync_PinnedEntriesRefreshWithoutRecentRequest_OthersDoNot()
    {
        var (warmup, cache) = Build();
        await warmup.PrimeAsync(TestContext.Current.CancellationToken);

        // Nobody asked for anything within the active window.
        var refreshed = await cache.RefreshDueAsync(
            TimeSpan.FromHours(1), TimeSpan.Zero, TestContext.Current.CancellationToken, includePinned: true);

        // window + failure-causes for three windows; the duration trend is not pinned.
        refreshed.Should().Be(6);
    }
}
