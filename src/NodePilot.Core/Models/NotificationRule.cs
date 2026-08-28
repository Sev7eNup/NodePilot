using NodePilot.Core.Enums;

namespace NodePilot.Core.Models;

/// <summary>
/// An alerting rule: "when an event of one of <see cref="EventTypes"/> happens and the
/// <see cref="FilterExpressionJson"/> filter matches, deliver via every <see cref="Routes"/>".
///
/// <para>Matching has two layers: a cheap coarse pre-filter on event type (<see cref="EventTypes"/>),
/// then a composable AND/OR/NOT filter tree over the event fields (the same condition AST the
/// designer uses for edge conditions, stored as a JSON string here). Throttling (cooldown, dedup
/// and flap suppression) keeps a single incident from fanning out into many alerts.</para>
/// </summary>
public class NotificationRule
{
    public Guid Id { get; set; }

    /// <summary>
    /// Which alerting kind owns this row (ADR 0008). <see cref="NotificationRuleKind.Custom"/> is a
    /// free-filter rule and the default; <see cref="NotificationRuleKind.System"/> is a policy bound to
    /// an <c>ISystemAlertSource</c> via <see cref="SystemSourceId"/>. The custom and system management
    /// surfaces each filter on this so neither mutates the other's rows.
    /// </summary>
    public NotificationRuleKind Kind { get; set; } = NotificationRuleKind.Custom;

    /// <summary>For <see cref="NotificationRuleKind.System"/> policies: the bound source's stable id. Null for Custom.</summary>
    public string? SystemSourceId { get; set; }

    /// <summary>For System policies: the preset the policy was seeded from, if any (informational). Null for Custom.</summary>
    public string? SystemPresetId { get; set; }

    /// <summary>
    /// For System policies: descriptor-validated source query parameters as a JSON object (e.g. a lookback
    /// window). Shapes what the source measures; the filter still decides whether a measurement alerts. Null
    /// for Custom or a parameter-less source.
    /// </summary>
    public string? SourceParametersJson { get; set; }

    /// <summary>
    /// For System policies: the condition must hold continuously for this many seconds before an episode
    /// opens (debounce). 0 fires on the first matching observation. Ignored for Custom rules. Resolution
    /// is bounded by the dispatcher pass interval.
    /// </summary>
    public int SustainForSeconds { get; set; }

    /// <summary>
    /// Optional per-policy severity override applied to the delivered notification, replacing the severity
    /// suggested by the source observation. Null keeps the observation's severity.
    /// </summary>
    public NotificationSeverity? SeverityOverride { get; set; }

    /// <summary>
    /// For System policies: the UTC instant the policy was (re-)activated. Event sources only alert on
    /// observations at or after this instant, so a policy enabled later never alerts on history that a
    /// sibling policy already moved the shared source cursor past. Null for Custom or never activated.
    /// </summary>
    public DateTime? ActivatedAt { get; set; }

    /// <summary>Unique, human-readable label (UI + audit).</summary>
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Master switch: a disabled rule never fires.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Coarse pre-filter: comma-separated <see cref="NotificationEventType"/> names this rule
    /// reacts to (e.g. "ExecutionFailed,ExecutionCancelled"). The dispatcher filters on this
    /// before evaluating the (more expensive) filter tree.
    /// </summary>
    public string EventTypes { get; set; } = string.Empty;

    /// <summary>
    /// Composable AND/OR/NOT filter over the event fields, using the same structured condition AST
    /// the designer uses for edge conditions, with operands of <c>source: "event"</c>. Stored as a
    /// JSON string to stay provider-agnostic and avoid EF <c>JsonElement</c> mapping pitfalls. Null
    /// or empty means no extra filter, matching every event of the configured types in scope.
    /// </summary>
    public string? FilterExpressionJson { get; set; }

    public NotificationScopeKind ScopeKind { get; set; } = NotificationScopeKind.Global;

    // --- Throttle ---
    /// <summary>Minimum minutes between alerts for the same dedup key. 0 disables the cooldown.</summary>
    public int CooldownMinutes { get; set; }

    /// <summary>
    /// Optional template for the dedup key. Null falls back to <c>ruleId + workflowId + eventType</c>.
    /// Reserved for a future custom-grouping feature.
    /// </summary>
    public string? DedupKeyTemplate { get; set; }

    /// <summary>Flap suppression: only fire once at least this many matching occurrences land
    /// within <see cref="OccurrenceWindowMinutes"/>. 1 fires on the first occurrence.</summary>
    public int MinOccurrences { get; set; } = 1;

    /// <summary>Rolling window (minutes) for <see cref="MinOccurrences"/>. 0 disables the threshold.</summary>
    public int OccurrenceWindowMinutes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }

    public ICollection<NotificationRoute> Routes { get; set; } = [];
    public ICollection<NotificationRuleTarget> Targets { get; set; } = [];
}
