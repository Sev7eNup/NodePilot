using NodePilot.Core.Enums;

namespace NodePilot.Core.Models;

/// <summary>
/// One raw observation from an <c>ISystemAlertSource</c> (the pluggable design from ADR 0008).
/// Sources report measurements or events without deciding health; the central evaluator applies
/// each policy's condition and sustain window to these. <see cref="InstanceKey"/> is the stable
/// identity the evaluator keys transient policy state by. <see cref="Fields"/> holds the
/// normalized values a policy condition can address; its keys must be a subset of the source
/// descriptor's field names.
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
    // For event sources: when the underlying event happened. The evaluator drops observations
    // older than a policy's activation instant, so a late-activated policy never alerts on old
    // history (ADR 0008). Null for metric sources (a level, not an event), which never back-alert
    // because their sustain window starts now.
    DateTime? OccurredAt = null,
    // Optional numeric measurement surfaced as the delivered event's signalValue (e.g. backlog
    // depth). Null when the source has no single headline number.
    long? SignalValue = null);
