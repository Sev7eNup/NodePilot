using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodePilot.Api.Services;
using NodePilot.Core.Interfaces;
using NodePilot.Data;
using Xunit;

namespace NodePilot.Api.Tests.Services;

/// <summary>
/// Covers <see cref="DashboardAggregateCache"/>. The dashboard is a shared, folder-scoped surface,
/// so the two properties that matter are that callers with different permissions never share an
/// entry, and that a burst of concurrent misses does not multiply the work the cache exists to
/// remove.
/// </summary>
public sealed class DashboardAggregateCacheTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public DashboardAggregateCacheTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<NodePilotDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private DashboardAggregateCache NewCache()
        => new(_provider.GetRequiredService<IServiceScopeFactory>());

    private static AccessibleFolderSet Scoped(params Guid[] folderIds)
        => new() { IsUnrestricted = false, FolderIds = [.. folderIds] };

    [Fact]
    public void ScopeKey_UnrestrictedAndScoped_AreDifferent()
    {
        // The whole point: an unrestricted (global Admin) aggregate must never be handed to a
        // caller who can only read some folders.
        var unrestricted = DashboardAggregateCache.ScopeKey(AccessibleFolderSet.Unrestricted);
        var scoped = DashboardAggregateCache.ScopeKey(Scoped(Guid.NewGuid()));

        unrestricted.Should().NotBe(scoped);
    }

    [Fact]
    public void ScopeKey_DifferentFolderSets_AreDifferent()
    {
        DashboardAggregateCache.ScopeKey(Scoped(Guid.NewGuid()))
            .Should().NotBe(DashboardAggregateCache.ScopeKey(Scoped(Guid.NewGuid())));
    }

    [Fact]
    public void ScopeKey_SameFoldersEnumeratedDifferently_Match()
    {
        // A HashSet has no defined order; the key must not depend on it, or the same caller
        // would miss the cache at random.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        DashboardAggregateCache.ScopeKey(Scoped(a, b))
            .Should().Be(DashboardAggregateCache.ScopeKey(Scoped(b, a)));
    }

    [Fact]
    public void Key_DifferentWindows_AreDifferent()
    {
        var accessible = AccessibleFolderSet.Unrestricted;

        DashboardAggregateCache.Key("window", accessible, 24)
            .Should().NotBe(DashboardAggregateCache.Key("window", accessible, 720));
    }

    [Fact]
    public async Task GetOrComputeAsync_WithinTtl_ComputesOnce()
    {
        var cache = NewCache();
        var calls = 0;

        for (var i = 0; i < 3; i++)
        {
            await cache.GetOrComputeAsync("k", TimeSpan.FromMinutes(5),
                (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(7); },
                CancellationToken.None);
        }

        calls.Should().Be(1);
    }

    [Fact]
    public async Task GetOrComputeAsync_ExpiredEntry_Recomputes()
    {
        var cache = NewCache();
        var calls = 0;

        async Task<int> Read() => await cache.GetOrComputeAsync("k", TimeSpan.Zero,
            (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(1); },
            CancellationToken.None);

        await Read();
        await Read();

        calls.Should().Be(2);
    }

    [Fact]
    public async Task GetOrComputeAsync_ConcurrentMisses_ShareOneComputation()
    {
        // Without this, expiry on a busy instance produces a thundering herd of identical
        // aggregations — the exact load the cache is meant to remove.
        var cache = NewCache();
        var calls = 0;
        var release = new TaskCompletionSource();

        async Task<int> Read() => await cache.GetOrComputeAsync("k", TimeSpan.FromMinutes(5),
            async (_, _) =>
            {
                Interlocked.Increment(ref calls);
                await release.Task;
                return 42;
            },
            CancellationToken.None);

        var readers = Enumerable.Range(0, 8).Select(_ => Read()).ToArray();
        release.SetResult();
        var results = await Task.WhenAll(readers);

        calls.Should().Be(1);
        results.Should().AllBeEquivalentTo(42);
    }

    [Fact]
    public async Task GetOrComputeAsync_ComputationFails_IsNotCachedAsAnAnswer()
    {
        var cache = NewCache();
        var calls = 0;

        async Task<int> Read() => await cache.GetOrComputeAsync("k", TimeSpan.FromMinutes(5),
            (_, _) =>
            {
                var attempt = Interlocked.Increment(ref calls);
                return attempt == 1
                    ? Task.FromException<int>(new InvalidOperationException("boom"))
                    : Task.FromResult(5);
            },
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(Read);
        (await Read()).Should().Be(5);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task GetOrComputeAsync_OneCallerCancels_OthersStillGetTheResult()
    {
        // A browser navigating away cancels its request. That must not cancel the shared
        // computation the remaining callers are waiting on.
        var cache = NewCache();
        var release = new TaskCompletionSource();
        var leaving = new CancellationTokenSource();

        async Task<int> Read(CancellationToken ct) => await cache.GetOrComputeAsync(
            "k", TimeSpan.FromMinutes(5),
            async (_, _) => { await release.Task; return 11; }, ct);

        var abandoned = Read(leaving.Token);
        var waiting = Read(CancellationToken.None);
        await leaving.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        release.SetResult();
        (await waiting).Should().Be(11);
    }

    [Fact]
    public async Task PrimeAsync_MakesTheFirstCallerAHit()
    {
        // The whole point of pre-computing: whoever opens the dashboard first must not pay for the
        // aggregation.
        var cache = NewCache();
        var computations = 0;

        Task<int> Compute(NodePilotDbContext _, CancellationToken __)
        {
            Interlocked.Increment(ref computations);
            return Task.FromResult(3);
        }

        await cache.PrimeAsync("k", TimeSpan.FromMinutes(5), Compute, CancellationToken.None);
        var value = await cache.GetOrComputeAsync("k", TimeSpan.FromMinutes(5), Compute, CancellationToken.None);

        value.Should().Be(3);
        computations.Should().Be(1, "the priming pass is the only computation");
    }

    [Fact]
    public async Task RefreshDueAsync_PrimedEntry_IsRenewedBeforeItExpires()
    {
        // Regression guard. Priming used to leave LastRequestedUtc unset, which made the refresh
        // filter skip the entry forever: it expired one TTL after startup and was then dropped, so
        // the warm-up only ever helped callers arriving in that first window.
        var cache = NewCache();
        var computations = 0;

        Task<int> Compute(NodePilotDbContext _, CancellationToken __)
        {
            Interlocked.Increment(ref computations);
            return Task.FromResult(1);
        }

        await cache.PrimeAsync("k", TimeSpan.Zero, Compute, CancellationToken.None);
        var refreshed = await cache.RefreshDueAsync(
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), CancellationToken.None);

        refreshed.Should().Be(1, "a primed entry must stay eligible for renewal");
        computations.Should().Be(2);
    }

    [Fact]
    public async Task RefreshDueAsync_RequestedAndExpiring_IsRefreshed()
    {
        var cache = NewCache();
        var computations = 0;

        Task<int> Compute(NodePilotDbContext _, CancellationToken __)
        {
            Interlocked.Increment(ref computations);
            return Task.FromResult(1);
        }

        // TimeSpan.Zero: already expired, so it is due.
        await cache.GetOrComputeAsync("k", TimeSpan.Zero, Compute, CancellationToken.None);
        var refreshed = await cache.RefreshDueAsync(
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), CancellationToken.None);

        refreshed.Should().Be(1);
        computations.Should().Be(2);
    }

    [Fact]
    public async Task RefreshDueAsync_RequestedLongAgo_IsDroppedNotRefreshed()
    {
        var cache = NewCache();
        var computations = 0;

        Task<int> Compute(NodePilotDbContext _, CancellationToken __)
        {
            Interlocked.Increment(ref computations);
            return Task.FromResult(1);
        }

        await cache.GetOrComputeAsync("k", TimeSpan.Zero, Compute, CancellationToken.None);

        // An activity window of zero makes the single entry look abandoned.
        var refreshed = await cache.RefreshDueAsync(
            TimeSpan.FromMinutes(1), TimeSpan.Zero, CancellationToken.None);

        refreshed.Should().Be(0);
        computations.Should().Be(1);
        cache.Count.Should().Be(0, "abandoned entries are dropped rather than kept forever");
    }

    [Fact]
    public async Task GetOrComputeAsync_ProvidesAUsableDbContext()
    {
        // The computation must not depend on the calling request's scope: that context can be
        // disposed while other callers still await the shared result.
        var cache = NewCache();

        var count = await cache.GetOrComputeAsync("k", TimeSpan.FromMinutes(5),
            async (db, ct) => await db.Workflows.CountAsync(ct),
            CancellationToken.None);

        count.Should().Be(0);
    }
}
