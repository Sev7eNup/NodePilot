using NodePilot.Core.Enums;

namespace NodePilot.Core.Models;

/// <summary>
/// Cooldown, dedup and flap-suppression state, one row per (rule, dedup key). Decides whether a
/// rule may fire again for a key; the delivery ledger holds the per-occurrence history instead.
/// Unique on (RuleId, DedupKey).
/// </summary>
public class NotificationSuppressionState
{
    public Guid Id { get; set; }
    public Guid NotificationRuleId { get; set; }
    public string DedupKey { get; set; } = string.Empty;
    public DateTime? LastFiredAt { get; set; }
    /// <summary>Count of matching occurrences inside the current flap window.</summary>
    public int OccurrenceCount { get; set; }
    /// <summary>
    /// Start of the current flap window, used by MinOccurrences and OccurrenceWindowMinutes.
    /// </summary>
    public DateTime? WindowStartedAt { get; set; }
}

/// <summary>
/// Per-occurrence, per-route delivery history and idempotency guard. Unique on
/// (RuleId, RouteId, EventKey) so a crash and rescan never double-sends the same occurrence to
/// the same route: the matcher inserts a Pending row idempotently before the sender does any I/O.
/// <see cref="NotificationSuppressionState"/> covers rate limiting instead of exactly-once.
/// </summary>
public class NotificationDeliveryAttempt
{
    public Guid Id { get; set; }
    public Guid NotificationRuleId { get; set; }
    public Guid NotificationRouteId { get; set; }
    /// <summary>Stable per-occurrence key (e.g. <c>exec:{executionId}:{eventType}</c>).</summary>
    public string EventKey { get; set; } = string.Empty;
    public string DedupKey { get; set; } = string.Empty;
    public NotificationDeliveryStatus Status { get; set; } = NotificationDeliveryStatus.Pending;
    public int Attempt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
    public string? Error { get; set; }
    /// <summary>True for test-fire deliveries — they never touch suppression state.</summary>
    public bool IsTest { get; set; }
    /// <summary>Redacted one-line summary of what was sent (for the UI/ledger view).</summary>
    public string? Summary { get; set; }
}

/// <summary>
/// Per-policy, per-instance match state for a system-alert policy (ADR 0008). One row per
/// (policy, source, instance), unique on (NotificationRuleId, SourceId, InstanceKey). Tracks
/// whether the condition holds, when it started holding, when the alertable episode opened, and
/// when the instance was last seen. Cleared when the policy is disabled or its source, params,
/// filter, scope or duration change.
/// </summary>
public class SystemAlertPolicyState
{
    public Guid Id { get; set; }
    public Guid NotificationRuleId { get; set; }
    public string SourceId { get; set; } = string.Empty;
    public string InstanceKey { get; set; } = string.Empty;
    /// <summary>Whether the policy's condition held for this instance at the last evaluation.</summary>
    public bool IsMatching { get; set; }
    /// <summary>When the condition first began holding continuously (start of the sustain window). Null when not matching.</summary>
    public DateTime? MatchStartedAt { get; set; }
    /// <summary>Start of the current alertable episode (sustain satisfied). Null until an episode opens.</summary>
    public DateTime? EpisodeStartedAt { get; set; }
    /// <summary>Last time this instance was seen in an observation — drives stale-instance retention.</summary>
    public DateTime LastObservedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Coarse per-source cursor state shared across a source's policies (ADR 0008) — e.g. the terminal-execution
/// scan watermark. Distinct from <see cref="SystemAlertPolicyState"/> (which is per policy): a source samples
/// once per pass regardless of how many policies read it. Unique on (SourceId, StateKey).
/// </summary>
public class SystemAlertSourceState
{
    public Guid Id { get; set; }
    public string SourceId { get; set; } = string.Empty;
    /// <summary>Discriminator within a source when it keeps more than one cursor (e.g. per normalized query). Empty = the source's single cursor.</summary>
    public string StateKey { get; set; } = string.Empty;
    /// <summary>Opaque cursor payload (JSON) owned by the source.</summary>
    public string? CursorJson { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Single-row watermark for the dispatcher's inclusive execution scan cursor. Persisted so the
/// dispatcher resumes after a restart without re-alerting everything. Scans
/// <c>(CompletedAt &gt; LastCompletedAtSeen) OR (== AND Id &gt; LastIdSeen)</c>.
/// </summary>
public class NotificationDispatcherState
{
    public Guid Id { get; set; }
    public DateTime? LastCompletedAtSeen { get; set; }
    public Guid? LastIdSeen { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Stable id for the singleton row.</summary>
    public static readonly Guid SingletonId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
}
