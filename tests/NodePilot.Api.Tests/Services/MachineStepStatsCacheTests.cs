using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodePilot.Api.Services;
using NodePilot.Data;
using Xunit;

namespace NodePilot.Api.Tests.Services;

public sealed class MachineStepStatsCacheTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly TestClock _clock = new();
    private static MachineStepStats Empty() => new(
        new Dictionary<Guid, (int Total, int Failed)>(), new Dictionary<Guid, int>());

    public MachineStepStatsCacheTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(o => o.UseSqlite("Data Source=:memory:"));
        _provider = services.BuildServiceProvider();
    }

    public void Dispose() => _provider.Dispose();

    private MachineStepStatsCache NewCache(
        Func<NodePilotDbContext, DateTime, CancellationToken, Task<MachineStepStats>> read,
        CancellationToken stoppingToken = default)
        => new(_provider.GetRequiredService<IServiceScopeFactory>(), stoppingToken, _clock, read);

    [Fact]
    public async Task ConcurrentMisses_ShareOneComputation()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var expected = Empty();
        var cache = NewCache(async (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return expected;
        });

        var readers = Enumerable.Range(0, 8).Select(_ => cache.GetAsync(TestContext.Current.CancellationToken)).ToArray();
        calls.Should().Be(1);
        release.SetResult();
        var results = await Task.WhenAll(readers);
        results.Should().OnlyContain(r => ReferenceEquals(r, expected));
    }

    [Fact]
    public async Task RequestCancellationAndScopeDisposal_DoNotCancelSharedWork()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var leaving = new CancellationTokenSource();
        NodePilotDbContext? computationDb = null;
        var cache = NewCache(async (db, _, token) =>
        {
            computationDb = db;
            await release.Task.WaitAsync(token);
            db.ChangeTracker.Entries().Should().BeEmpty();
            return Empty();
        });

        Task<MachineStepStats> abandoned;
        using (var requestScope = _provider.CreateScope())
        {
            var requestDb = requestScope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
            abandoned = cache.GetAsync(leaving.Token);
            computationDb.Should().NotBeSameAs(requestDb);
            await leaving.CancelAsync();
        }
        var waiting = cache.GetAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        release.SetResult();
        await waiting;

        var readDisposed = () => computationDb!.ChangeTracker.Entries().ToList();
        readDisposed.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task Ttl_StartsAtCompletion_AndExpiresAfterTenSeconds()
    {
        var calls = 0;
        var cache = NewCache((_, _, _) =>
        {
            calls++;
            _clock.Advance(TimeSpan.FromSeconds(20));
            return Task.FromResult(Empty());
        });

        var first = await cache.GetAsync(TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(9));
        (await cache.GetAsync(TestContext.Current.CancellationToken)).Should().BeSameAs(first);
        calls.Should().Be(1);
        _clock.Advance(TimeSpan.FromSeconds(1));
        (await cache.GetAsync(TestContext.Current.CancellationToken)).Should().NotBeSameAs(first);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task FailedComputation_IsRetriedByTheNextCaller()
    {
        var calls = 0;
        var cache = NewCache((_, _, _) => ++calls == 1
            ? Task.FromException<MachineStepStats>(new InvalidOperationException("failed"))
            : Task.FromResult(Empty()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(TestContext.Current.CancellationToken));
        await cache.GetAsync(TestContext.Current.CancellationToken);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task HostShutdown_CancelsTheSharedComputation()
    {
        using var shutdown = new CancellationTokenSource();
        var cache = NewCache(async (_, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Empty();
        }, shutdown.Token);

        var pending = cache.GetAsync(TestContext.Current.CancellationToken);
        await shutdown.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
