using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NodePilot.Api.Hosting;
using NodePilot.Core.Interfaces;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

/// <summary>
/// Unit tests for the fail-closed leader-health computation that backs <c>/healthz/leader</c>.
/// Driven by a <see cref="FakeProvider"/> so each fail-closed condition can be exercised
/// in isolation without spinning up a full WebApplicationFactory.
/// </summary>
public class ClusterSetupTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private static readonly DateTime Now = new(2026, 5, 9, 12, 0, 0, DateTimeKind.Utc);

    private static int StatusCode(IResult result)
    {
        // Results.Ok / Results.Json return concrete IStatusCodeHttpResult or Ok<T> wrappers.
        return result switch
        {
            IStatusCodeHttpResult sc => sc.StatusCode ?? 0,
            _ => 0
        };
    }

    [Fact]
    public void ComputeLeaderHealth_AllConditionsHealthy_Returns200()
    {
        var p = new FakeProvider(isLeader: true,
            lastRenewAt: Now.AddSeconds(-5),
            leaseExpiresAt: Now.AddSeconds(25),
            leaseEpoch: 7);

        var result = ClusterSetup.ComputeLeaderHealth(p, Ttl, Now);

        StatusCode(result).Should().Be(200);
    }

    [Fact]
    public void ComputeLeaderHealth_NotLeader_Returns503()
    {
        var p = new FakeProvider(isLeader: false,
            lastRenewAt: null,
            leaseExpiresAt: null,
            leaseEpoch: 0);

        var result = ClusterSetup.ComputeLeaderHealth(p, Ttl, Now);

        StatusCode(result).Should().Be(503);
    }

    [Fact]
    public void ComputeLeaderHealth_StaleRenew_Returns503_EvenWhenIsLeaderTrue()
    {
        // The leader's process still believes it's the leader, but it has not been able to
        // renew the lease in the DB for longer than TTL. This is precisely the
        // DB-unreachable-but-process-still-up scenario fail-closed must catch.
        var p = new FakeProvider(isLeader: true,
            lastRenewAt: Now.AddSeconds(-31),  // > 30s TTL ago
            leaseExpiresAt: Now.AddSeconds(-1), // already expired
            leaseEpoch: 7);

        var result = ClusterSetup.ComputeLeaderHealth(p, Ttl, Now);

        StatusCode(result).Should().Be(503);
    }

    [Fact]
    public void ComputeLeaderHealth_LeaseExpiredButRenewFresh_Returns503()
    {
        // Edge case: a renew call returned RowsAffected=0 (so we technically lost the lease)
        // but in-memory state still says IsLeader=true momentarily before the next tick
        // fixes it. The endpoint must already report 503 because LeaseExpiresAt is in the
        // past — this is what makes the LB redirect immediately, not after the next tick.
        var p = new FakeProvider(isLeader: true,
            lastRenewAt: Now.AddSeconds(-2),
            leaseExpiresAt: Now.AddSeconds(-1),
            leaseEpoch: 7);

        var result = ClusterSetup.ComputeLeaderHealth(p, Ttl, Now);

        StatusCode(result).Should().Be(503);
    }

    [Fact]
    public void ComputeLeaderHealth_NeverRenewed_Returns503()
    {
        // Just-started follower: IsLeader is false, no renew has happened yet, no lease.
        var p = new FakeProvider(isLeader: false,
            lastRenewAt: null,
            leaseExpiresAt: null,
            leaseEpoch: 0);

        var result = ClusterSetup.ComputeLeaderHealth(p, Ttl, Now);

        StatusCode(result).Should().Be(503);
    }

    private sealed class FakeProvider : IClusterStateProvider
    {
        public FakeProvider(bool isLeader, DateTime? lastRenewAt, DateTime? leaseExpiresAt,
            long leaseEpoch)
        {
            IsLeader = isLeader;
            LastSuccessfulRenewAt = lastRenewAt;
            LeaseExpiresAt = leaseExpiresAt;
            LeaseEpoch = leaseEpoch;
        }
        public bool IsLeader { get; }
        public string NodeId => "test-node";
        public DateTime? LeaseExpiresAt { get; }
        public long LeaseEpoch { get; }
        public DateTime? LastSuccessfulRenewAt { get; }
        public event Action<long>? OnLeadershipAcquired { add { } remove { } }
        public event Action? OnLeadershipLost { add { } remove { } }
    }
}
