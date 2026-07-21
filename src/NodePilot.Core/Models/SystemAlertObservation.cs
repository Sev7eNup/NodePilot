using NodePilot.Core.Enums;

namespace NodePilot.Core.Models;

/// <summary>
/// One raw observation from an <c>ISystemAlertSource</c> — the pluggable alert-source design from ADR 0008.
/// Sources report measurements/events WITHOUT deciding health — the central evaluator applies each policy's
/// condition and sustain window to these. <see cref="InstanceKey"/> is the stable identity the evaluator keys transient policy state by:
/// per credential, per workflow+node, per execution, or a constant for a global singleton (e.g. "backlog").
/// <see cref="Fields"/> holds the normalized values addressable from a policy condition — keys must be a
/// subset of the source descriptor's field names.
/// </summary>
public sealed record SystemAlertObservation(
    string SourceId,
    string InstanceKey,
    NotificationSeverity SeveritySuggestion,
    string Title,
    string Summary,
    string DeepLinkPath,
    IReadOnlyDictionary<string, object?> Fields,
    Guid? WorkflowId = null,
    string? WorkflowName = null,
    Guid? FolderId = null,
    string? FolderPath = null,
    string? TargetMachine = null,
    // For event sources: when the underlying event happened. The evaluator drops observations older than a
    // policy's activation instant so a late-activated policy never back-alerts history (ADR 0008). Null for
    // metric sources (a level, not an event) — those never back-alert because their sustain window starts now.
    DateTime? OccurredAt = null,
    // Optional numeric measurement surfaced as the delivered event's signalValue (e.g. backlog depth). Null
    // when the source has no single headline number.
    long? SignalValue = null);
