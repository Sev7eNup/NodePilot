namespace NodePilot.Core.Models;

/// <summary>
/// Append-only cache of handled external-trigger requests plus short-lived webhook replay claims.
/// External-trigger rows hold a domain-separated digest of the authenticated key principal and
/// the client-supplied <c>Idempotency-Key</c>; webhook rows hold their own keyed digest with an
/// empty execution ID. The shared unique index makes the insert an atomic, cluster-wide nonce
/// guard, and rows expire at <see cref="ExpiresAt"/> and are pruned by
/// <c>IdempotencyKeyCleanupService</c>.
/// </summary>
public class IdempotencyKey
{
    public Guid Id { get; set; }

    /// <summary>
    /// Domain-separated replay key. External triggers store a digest instead of the raw header;
    /// other producers sharing this table use their own prefixed form.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Target workflow. Keys are partitioned by workflow, so the same digest on another
    /// workflow is a separate row.</summary>
    public Guid WorkflowId { get; set; }

    /// <summary>Execution created on the first request; returned on every replay.</summary>
    public Guid ExecutionId { get; set; }

    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the row becomes pruneable. Defaults to 24 hours after it was first seen, covering the
    /// retry window of typical webhook senders.
    /// </summary>
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddHours(24);
}
