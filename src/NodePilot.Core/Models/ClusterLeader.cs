namespace NodePilot.Core.Models;

/// <summary>
/// Single-row table that holds the active/passive HA leader lease. Exactly one row exists,
/// keyed by <see cref="Resource"/> = "primary". The active node updates ExpiresAt periodically
/// to renew the lease; if a node fails, the lease times out and the other node takes over by
/// atomically updating the row when <see cref="ExpiresAt"/> &lt; db_now.
/// <para>
/// Concurrency is handled by atomic <c>UPDATE ... WHERE</c> against this row, not by EF
/// Core's optimistic-concurrency token. Provider-specific row-version mappings
/// (SQL Server <c>rowversion</c> vs. Postgres <c>xmin</c>) cannot be expressed in a single
/// provider-agnostic migration, which is why this table uses an explicit monotonic
/// <see cref="LeaseEpoch"/> as a fencing token instead.
/// </para>
/// </summary>
public class ClusterLeader
{
    /// <summary>Always "primary" today; reserved for future per-resource leases. PK.</summary>
    public string Resource { get; set; } = "primary";

    /// <summary>
    /// Identifier of the node that currently holds the lease. Empty string when unowned
    /// (typical only on a freshly-seeded row before the first acquisition).
    /// </summary>
    public string OwnerNodeId { get; set; } = string.Empty;

    /// <summary>UTC timestamp at which the current owner first acquired the lease.</summary>
    public DateTime AcquiredAt { get; set; }

    /// <summary>
    /// UTC timestamp at which the current lease expires unless renewed. The follower polls
    /// for <c>ExpiresAt &lt; db_now</c> to take over.
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>UTC timestamp of the most recent successful renewal.</summary>
    public DateTime LastRenewedAt { get; set; }

    /// <summary>
    /// Monotonic fencing token. Incremented by exactly 1 each time a node *acquires* the
    /// lease (not on renewal). Audit events emit this value so the leader-handoff history is
    /// reconstructable. Future: long-running operations can read the epoch at start and
    /// validate it before commit, refusing to write if the lease has rolled over to another
    /// node since the operation began.
    /// </summary>
    public long LeaseEpoch { get; set; }
}
