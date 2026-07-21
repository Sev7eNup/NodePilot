using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Scheduler.Cluster;
using Xunit;

namespace NodePilot.Engine.Tests.Cluster;

/// <summary>
/// Unit tests for the active/passive lease-acquisition logic. The service is exercised
/// directly by reflection-invoking its private <c>TickAsync</c>, so the renew/acquire
/// pathways are testable without spinning up the BackgroundService loop and racing against
/// real wall-clock delays.
/// <para>
/// Each test uses a single shared in-memory SQLite connection so multiple service instances
/// see the same row — this is what makes "two nodes racing for one lease" expressible in a
/// unit test.
/// </para>
/// </summary>
public sealed class ClusterLeaderServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly NodePilotDbContext _seedContext;

    public ClusterLeaderServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(_connection).Options;
        _seedContext = new NodePilotDbContext(options);
        _seedContext.Database.EnsureCreated();

        // Seed an expired primary row so a fresh node can immediately acquire.
        _seedContext.ClusterLeaders.Add(new ClusterLeader
        {
            Resource = "primary",
            OwnerNodeId = string.Empty,
            AcquiredAt = DateTime.MinValue.ToUniversalTime(),
            ExpiresAt = DateTime.MinValue.ToUniversalTime(),
            LastRenewedAt = DateTime.MinValue.ToUniversalTime(),
            LeaseEpoch = 0
        });
        _seedContext.SaveChanges();
    }

    public void Dispose()
    {
        _seedContext.Dispose();
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private (ClusterLeaderService svc, IServiceScopeFactory scopeFactory) BuildService(string nodeId,
        int ttlSec = 30, int renewSec = 10, TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(opts => opts.UseSqlite(_connection),
            ServiceLifetime.Transient);
        var sp = services.BuildServiceProvider();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cluster:Enabled"] = "true",
                ["Cluster:NodeId"] = nodeId,
                ["Cluster:LeaseTtlSeconds"] = ttlSec.ToString(),
                ["Cluster:LeaseRenewSeconds"] = renewSec.ToString(),
                ["Cluster:LeaseDbTimeoutSeconds"] = "3"
            })
            .Build();

        var svc = new ClusterLeaderService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            config,
            NullLogger<ClusterLeaderService>.Instance,
            timeProvider);
        return (svc, sp.GetRequiredService<IServiceScopeFactory>());
    }

    private static Task TickAsync(ClusterLeaderService svc)
    {
        var method = typeof(ClusterLeaderService).GetMethod("TickAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (Task)method.Invoke(svc, new object[] { CancellationToken.None })!;
    }

    [Fact]
    public async Task Tick_FreshSeedRow_AcquiresLease_AndRaisesAcquiredEvent()
    {
        var (svc, _) = BuildService("node-a");
        long? observedEpoch = null;
        svc.OnLeadershipAcquired += epoch => observedEpoch = epoch;

        await TickAsync(svc);

        svc.IsLeader.Should().BeTrue();
        svc.LeaseEpoch.Should().Be(1, "epoch increments by 1 on each acquisition (seed was 0)");
        svc.LastSuccessfulRenewAt.Should().NotBeNull();
        observedEpoch.Should().Be(1);

        var row = _seedContext.ClusterLeaders.AsNoTracking().Single(r => r.Resource == "primary");
        row.OwnerNodeId.Should().Be("node-a");
    }

    [Fact]
    public async Task Tick_TwoNodes_SeedRow_ExactlyOneAcquires()
    {
        var (a, _) = BuildService("node-a");
        var (b, _) = BuildService("node-b");

        // Two ticks fired sequentially — but both services see an expired seed row at start,
        // so without atomic UPDATE WHERE both would think they won. The atomic clause in
        // the service ensures the second tick gets RowsAffected=0.
        await TickAsync(a);
        await TickAsync(b);

        var winners = new[] { a, b }.Where(s => s.IsLeader).ToList();
        winners.Should().HaveCount(1, "exactly one node may hold the primary lease at a time");

        // Lease-Epoch should have moved to 1 — only one acquisition happened.
        var row = _seedContext.ClusterLeaders.AsNoTracking().Single(r => r.Resource == "primary");
        row.LeaseEpoch.Should().Be(1);
    }

    [Fact]
    public async Task Tick_LeaderRenews_DoesNotIncrementEpoch()
    {
        var (svc, _) = BuildService("node-a");

        await TickAsync(svc);   // acquire (epoch 0 → 1)
        var afterAcquire = svc.LeaseEpoch;

        await TickAsync(svc);   // renew, must NOT increment

        svc.LeaseEpoch.Should().Be(afterAcquire, "renew is just an ExpiresAt update, no epoch bump");
        svc.IsLeader.Should().BeTrue();
    }

    [Fact]
    public async Task Tick_LeaderLosesLease_FollowerTakesOver_EpochIncrements()
    {
        var (a, _) = BuildService("node-a");
        var (b, _) = BuildService("node-b");

        await TickAsync(a);
        a.IsLeader.Should().BeTrue();
        var aEpoch = a.LeaseEpoch;

        // Simulate node-a crashing: expire its lease in the DB without any graceful release.
        _seedContext.Database.ExecuteSqlRaw(@"
            UPDATE ""ClusterLeaders""
               SET ""ExpiresAt"" = '1970-01-01 00:00:00'
             WHERE ""Resource"" = 'primary'");

        // Node-b now sees an expired lease and acquires.
        await TickAsync(b);
        b.IsLeader.Should().BeTrue();
        b.LeaseEpoch.Should().Be(aEpoch + 1, "epoch must monotonically increase on every acquire");

        // Node-a's *next* tick will try to renew, find RowsAffected=0, and step down.
        bool aLossEvent = false;
        a.OnLeadershipLost += () => aLossEvent = true;
        await TickAsync(a);
        a.IsLeader.Should().BeFalse("renew must fail when another node has taken the lease");
        aLossEvent.Should().BeTrue("OnLeadershipLost event must fire on takeover-by-other-node");
    }

    [Fact]
    public async Task Tick_BothNodesIdle_NeitherAcquiresWhenLeaseValid()
    {
        var (a, _) = BuildService("node-a");
        var (b, _) = BuildService("node-b");

        await TickAsync(a);
        a.IsLeader.Should().BeTrue();

        // While a's lease is fresh and valid, b's tick must NOT acquire.
        await TickAsync(b);
        b.IsLeader.Should().BeFalse("a fresh lease must block other nodes from acquiring");
    }

    [Fact]
    public async Task IsLeader_FailsClosedWhenLocalLeaseExpiresDuringVmPause()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var (service, _) = BuildService("node-a", ttlSec: 30, timeProvider: clock);
        await TickAsync(service);
        service.IsLeader.Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(31));

        service.IsLeader.Should().BeFalse(
            "a paused old leader must fence itself before its next database renew tick");
    }

    [Fact]
    public async Task StopAsync_ReleasesLease_FasterFailover()
    {
        var (a, _) = BuildService("node-a");
        var (b, _) = BuildService("node-b");

        await TickAsync(a);
        await a.StopAsync(CancellationToken.None);

        // After graceful stop, b's tick should immediately succeed even though TTL hasn't
        // elapsed in wall-clock terms — StopAsync expired the lease in-DB.
        await TickAsync(b);
        b.IsLeader.Should().BeTrue("StopAsync must release the lease so the standby can take over instantly");
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;

        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long GetTimestamp() => _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan elapsed)
        {
            _utcNow += elapsed;
            _timestamp += elapsed.Ticks;
        }
    }
}
