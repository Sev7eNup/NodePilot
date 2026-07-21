using NodePilot.Core.Enums;

namespace NodePilot.Core.Models;

/// <summary>
/// Cooldown / dedup / flap-suppression state, one row per (rule, dedup key). Separate from the
/// delivery ledger on purpose: this answers "may this rule fire again for this key yet?", whereas
/// the ledger is the per-occurrence history. Unique on (RuleId, DedupKey).
/// </summary>
public class NotificationSuppressionState
{
    public Guid Id { get; set; }
    public Guid NotificationRuleId { get; set; }
    public string DedupKey { get; set; } = string.Empty;
    public DateTime? LastFiredAt { get; set; }
    /// <summary>Count of matching occurrences inside the current flap window.</summary>
    public int OccurrenceCount { get; set; }
    /// <summary>Start of the current flap window (for MinOccurrences / OccurrenceWindowMinutes).</summary>
    public DateTime? WindowStartedAt { get; set; }
}

/// <summary>
/// Per-occurrence, per-route delivery history AND the idempotency guard. Unique on
/// (RuleId, RouteId, EventKey) so a crash-and-rescan never double-sends the same occurrence to the
/// same route — the Matcher inserts a Pending row idempotently before the Sender does any I/O.
/// This is distinct from <see cref="NotificationSuppressionState"/> (cooldown), which is about
/// rate, not exactly-once.
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
/// Transient per-policy, per-instance match state for a System-alert policy (ADR 0008). One row per
/// (policy, source, instance) — e.g. one per credential, per workflow+node, or per execution. Tracks whether
/// the policy's condition currently holds for that instance, when it started holding (for the sustain
/// window), the current alertable episode's start (woven into the delivery key so a policy alerts at most
/// once per episode), and when the instance was last observed (for stale-instance pruning). Cleared wholesale
/// when the policy is disabled or its source/params/filter/scope/duration change. Unique on
/// (NotificationRuleId, SourceId, InstanceKey).
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
