namespace NodePilot.Core.Models;

/// <summary>
/// Single-row table that holds the active/passive HA leader lease, keyed by
/// <see cref="Resource"/> = "primary". The active node renews the lease by updating
/// <see cref="ExpiresAt"/>; when it fails, the lease times out and the other node takes over
/// by atomically updating the row once <see cref="ExpiresAt"/> &lt; db_now.
/// <para>
/// Concurrency relies on an atomic <c>UPDATE ... WHERE</c> against this row rather than EF
/// Core's optimistic-concurrency token, because provider-specific row-version mappings
/// (SQL Server <c>rowversion</c> vs. Postgres <c>xmin</c>) cannot be expressed in one
/// provider-agnostic migration. The explicit monotonic <see cref="LeaseEpoch"/> serves as the
/// fencing token instead.
/// </para>
/// </summary>
public class ClusterLeader
{
    /// <summary>Primary key. Always "primary"; reserved for per-resource leases.</summary>
    public string Resource { get; set; } = "primary";

    /// <summary>
    /// Identifier of the node that currently holds the lease. Empty string when unowned, which
    /// normally happens only on a freshly seeded row before the first acquisition.
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
    /// Monotonic fencing token. Incremented by one each time a node acquires the lease, not on
    /// renewal. Audit events emit the value so the leader-handoff history can be reconstructed.
    /// </summary>
    public long LeaseEpoch { get; set; }
}
