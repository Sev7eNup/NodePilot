namespace NodePilot.Core.Interfaces;

/// <summary>
/// Surfaces the active/passive HA cluster state to the rest of the app. Background services
/// gate their work on <see cref="IsLeader"/>; the load-balancer reads <c>/healthz/leader</c>
/// which is built on top of this interface.
/// <para>
/// In single-node mode (<c>Cluster:Enabled=false</c>) a no-op implementation always reports
/// <c>IsLeader=true</c>, so consumers don't need to special-case the cluster-disabled path.
/// </para>
/// </summary>
public interface IClusterStateProvider
{
    /// <summary>
    /// True only while this node holds a fresh lease that was successfully renewed within
    /// the TTL window. Read by every background service to decide whether to do work.
    /// </summary>
    bool IsLeader { get; }

    /// <summary>Stable identifier for this node, exposed in logs and audit details.</summary>
    string NodeId { get; }

    /// <summary>
    /// UTC timestamp at which the lease expires unless renewed. <c>null</c> on a follower.
    /// Used by the leader-health endpoint as one of its fail-closed conditions.
    /// </summary>
    DateTime? LeaseExpiresAt { get; }

    /// <summary>
    /// Monotonic fencing token. Increments by 1 each time *any* node acquires the lease
    /// (not on renew). Audit events emit this so the leader-handoff history is reconstructable.
    /// </summary>
    long LeaseEpoch { get; }

    /// <summary>
    /// UTC timestamp of the most recent successful DB-side lease renewal. <c>null</c> if no
    /// renewal has succeeded yet. The fail-closed leader-health endpoint refuses to answer
    /// 200 once <c>now − LastSuccessfulRenewAt &gt; LeaseTtl</c>, regardless of in-memory
    /// <see cref="IsLeader"/> state — so a leader that has lost DB connectivity cannot keep
    /// pretending to be the active node.
    /// </summary>
    DateTime? LastSuccessfulRenewAt { get; }

    /// <summary>
    /// Raised on the BackgroundService thread when the local node transitions from follower
    /// to leader. The <c>long</c> argument is the new <see cref="LeaseEpoch"/>. Subscribers
    /// must be fast and exception-tolerant; a slow handler delays the next renew tick.
    /// </summary>
    event Action<long>? OnLeadershipAcquired;

    /// <summary>
    /// Raised on the BackgroundService thread when the local node transitions from leader
    /// to follower (renewal failed, lease expired, or another node took over).
    /// </summary>
    event Action? OnLeadershipLost;
}
