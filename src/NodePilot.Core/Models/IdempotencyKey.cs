namespace NodePilot.Core.Models;

/// <summary>
/// Append-only cache of handled external-trigger requests plus short-lived webhook replay
/// claims. External-trigger rows use a domain-separated digest of the authenticated key
/// principal plus the client-supplied <c>Idempotency-Key</c> and point at an execution.
/// Webhook rows use their own domain-separated keyed digest and an empty execution ID; the
/// shared unique index provides an atomic, cluster-wide nonce guard.
///
/// Entries expire after <see cref="ExpiresAt"/> and are pruned by
/// <c>IdempotencyKeyCleanupService</c>. The key is scoped to <c>(Key, WorkflowId)</c>
/// so a genuinely different workflow can reuse the same digest unambiguously. For external
/// triggers the digest also partitions callers: two integration keys cannot replay or reserve
/// one another's token on the same workflow.
/// </summary>
public class IdempotencyKey
{
    public Guid Id { get; set; }

    /// <summary>
    /// Domain-separated replay key. External triggers store a digest rather than the raw header;
    /// other producers sharing this table use their own prefixed representation.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Target workflow — the key is partitioned by workflow so collisions across runbooks don't matter.</summary>
    public Guid WorkflowId { get; set; }

    /// <summary>Execution created on the first request — returned on every replay.</summary>
    public Guid ExecutionId { get; set; }

    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the cache row becomes pruneable. Default = 24 h after first seen, which matches
    /// what most retrying webhook senders will try before giving up.
    /// </summary>
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddHours(24);
}
