using System.Globalization;
using NodePilot.Core.Enums;

namespace NodePilot.Core.Models;

/// <summary>
/// An immutable occurrence to be matched against notification rules and rendered by sinks. Built by
/// the event collector; not a DB entity. <see cref="EventKey"/> is the stable per-occurrence id that
/// drives exactly-once delivery.
/// </summary>
public sealed record NotificationContext(
    NotificationEventType EventType,
    NotificationSeverity Severity,
    string EventKey,
    Guid? WorkflowId,
    string? WorkflowName,
    Guid? FolderId,
    string? FolderPath,
    Guid? ExecutionId,
    string? Status,
    string? ErrorMessage,
    long? DurationMs,
    DateTime OccurredAt,
    string? TriggeredBy,
    int CallDepth,
    bool IsSubWorkflow,
    string? TargetMachine,
    string? SourceKey,
    string? Title,
    string? Summary,
    string? DeepLinkPath,
    // Numeric measurement behind a gauge event (backlog depth, stale-age seconds, …). Null for
    // execution events. Exposed as the `signalValue` filter field so a rule can refine, e.g.
    // signalValue > 500. Optional + last so existing (named-arg) call sites are unaffected.
    long? SignalValue = null,
    // Who initiated a cancellation, for ExecutionCancelled events: "user" (manual single cancel),
    // "cancelAll", "failover", "reconciler", "dispatch", or "system". Empty for non-cancel events.
    // Exposed as the `cancelledBy` filter field so a rule can target manual cancels only.
    string? CancelledBy = null,
    // For SystemAlert events (ADR 0008): the emitting source's stable id (e.g. "backlog"). Exposed as
    // the `sourceId` filter field. Null for custom-rule events.
    string? SourceId = null,
    // For SystemAlert events: the source observation's own field values (e.g. depth=520, reachable=false),
    // merged into ToFieldMap so a policy condition and its route filters can address source-specific fields
    // by name — the fixed keys below only cover the custom-rule field catalog. Null for custom-rule events.
    IReadOnlyDictionary<string, string>? ExtraFields = null)
{
    /// <summary>
    /// Flattens the event into the string map the condition evaluator matches against (operands of
    /// <c>source: "event"</c>). The fixed keys MUST stay in sync with the frontend EVENT_FIELD_CATALOG;
    /// <see cref="ExtraFields"/> (system-alert source fields) are merged on top and win on key collision.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToFieldMap()
    {
        var inv = CultureInfo.InvariantCulture;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["eventType"] = EventType.ToString(),
            ["severity"] = Severity.ToString(),
            ["workflowName"] = WorkflowName ?? "",
            ["folderPath"] = FolderPath ?? "",
            ["status"] = Status ?? "",
            ["errorMessage"] = ErrorMessage ?? "",
            ["durationMs"] = DurationMs?.ToString(inv) ?? "",
            ["triggeredBy"] = TriggeredBy ?? "",
            ["callDepth"] = CallDepth.ToString(inv),
            ["isSubWorkflow"] = IsSubWorkflow ? "true" : "false",
            ["targetMachine"] = TargetMachine ?? "",
            ["sourceKey"] = SourceKey ?? "",
            ["signalValue"] = SignalValue?.ToString(inv) ?? "",
            ["cancelledBy"] = CancelledBy ?? "",
        };
        // System-alert source fields (incl. sourceId) arrive via ExtraFields — kept OUT of the fixed key set
        // so the custom-rule field catalog (and its frontend parity guard) stays at exactly these keys.
        if (ExtraFields is not null)
            foreach (var (k, v) in ExtraFields) map[k] = v;
        return map;
    }
}

/// <summary>Outcome of an <c>INotificationSink.SendAsync</c> call.</summary>
public sealed record NotificationSendResult(bool Success, string? Error = null)
{
    public static readonly NotificationSendResult Ok = new(true);
    public static NotificationSendResult Fail(string error) => new(false, error);
}
