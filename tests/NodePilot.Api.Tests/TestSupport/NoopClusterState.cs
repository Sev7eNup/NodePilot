using NodePilot.Core.Interfaces;

namespace NodePilot.Api.Tests.TestSupport;

/// <summary>
/// Always-leader <see cref="IClusterStateProvider"/> with inert leadership events — for
/// controller tests whose subject takes the provider as a dependency but never exercises
/// HA behavior. Previously duplicated across the three AdminSettings test files.
/// </summary>
public sealed class NoopClusterState : IClusterStateProvider
{
    public bool IsLeader => true;
    public string NodeId => "test-node";
    public DateTime? LeaseExpiresAt => null;
    public long LeaseEpoch => 0;
    public DateTime? LastSuccessfulRenewAt => null;
    public event Action<long>? OnLeadershipAcquired { add { } remove { } }
    public event Action? OnLeadershipLost { add { } remove { } }
}
