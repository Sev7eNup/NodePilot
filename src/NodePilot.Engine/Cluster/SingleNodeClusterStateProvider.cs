using NodePilot.Core.Interfaces;

namespace NodePilot.Engine.Cluster;

/// <summary>
/// Default <see cref="IClusterStateProvider"/> for single-node deployments
/// (<c>Cluster:Enabled=false</c>). Always reports <c>IsLeader=true</c> with a sentinel
/// epoch of 0 and a "renewal" timestamp pinned to UtcNow on each read, so the
/// fail-closed health-check always passes.
/// <para>
/// The events are never raised — there is no leadership to gain or lose in single-node mode.
/// </para>
/// </summary>
public sealed class SingleNodeClusterStateProvider : IClusterStateProvider
{
    public bool IsLeader => true;
    public string NodeId { get; }
    public DateTime? LeaseExpiresAt => DateTime.UtcNow.AddYears(100);
    public long LeaseEpoch => 0;
    public DateTime? LastSuccessfulRenewAt => DateTime.UtcNow;

    public event Action<long>? OnLeadershipAcquired { add { /* single-node: never fires */ } remove { /* single-node: never fires */ } }
    public event Action? OnLeadershipLost { add { /* single-node: never fires */ } remove { /* single-node: never fires */ } }

    public SingleNodeClusterStateProvider(string? nodeId = null)
    {
        NodeId = string.IsNullOrWhiteSpace(nodeId) ? Environment.MachineName : nodeId;
    }
}
