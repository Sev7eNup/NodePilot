namespace NodePilot.Core.Enums;

/// <summary>What happened that a notification rule can react to. Execution-* come from terminal
/// workflow executions; ExecutionRunningLong and ExecutionQueuedLong are detected by polling live
/// executions. ServiceStale, MachineUnreachable, BacklogHigh, PendingHigh and CancelRateHigh come
/// from health signals and fire per unhealthy episode; ScheduleMissed and WorkflowNoRecentSuccess
/// are signal-collected but workflow-scoped. Append-only: the names are the persisted contract in
/// NotificationRule.EventTypes and NotificationDeliveryAttempt.EventKey.</summary>
public enum NotificationEventType
{
    ExecutionFailed = 0,
    ExecutionSucceeded = 1,
    ExecutionCancelled = 2,
    ServiceStale = 3,
    MachineUnreachable = 4,
    BacklogHigh = 5,
    PendingHigh = 6,
    CancelRateHigh = 7,
    ExecutionRunningLong = 8,
    ExecutionQueuedLong = 9,
    ScheduleMissed = 10,
    WorkflowNoRecentSuccess = 11,
    CredentialFailure = 12,
    /// <summary>A credential's ExpiresAt is inside the warn window or already past.</summary>
    CredentialExpiring = 13,
    /// <summary>
    /// A modular system-alert policy episode (ADR 0008). The producer is identified by
    /// <c>NotificationContext.SourceId</c> rather than a per-source enum value, so this one family
    /// carries every <c>ISystemAlertSource</c>. It is absent from
    /// <c>NotificationRuleSemantics.SupportedEventTypes</c>, so the custom-rule API and editor never
    /// offer it; only the system-policy surface emits it.
    /// </summary>
    SystemAlert = 14,
}

/// <summary>
/// Which alerting generation owns a <c>NotificationRule</c> row. <see cref="Custom"/> = the
/// free-filter rules managed under <c>/api/alerting/rules</c>. <see cref="System"/> = a policy
/// bound to a modular <c>ISystemAlertSource</c> (ADR 0008), managed under
/// <c>/api/alerting/system</c>. Append-only; the value is the persisted contract and the
/// discriminator the two management surfaces filter on.
/// </summary>
public enum NotificationRuleKind
{
    Custom = 0,
    System = 1,
}

/// <summary>Delivery channel for a notification route.</summary>
public enum NotificationChannel
{
    Email = 0,
    GenericWebhook = 1,
    Teams = 2,
    Slack = 3,
    PagerDuty = 4,
    Opsgenie = 5,
}

public enum NotificationSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2,
}

/// <summary>Which workflows a rule applies to. Mirrors <see cref="MaintenanceScopeKind"/>.</summary>
public enum NotificationScopeKind
{
    Global = 0,
    Folders = 1,
    Workflows = 2,
}

/// <summary>Lifecycle of a single delivery attempt in the ledger.</summary>
public enum NotificationDeliveryStatus
{
    Pending = 0,
    Sent = 1,
    Failed = 2,
}

/// <summary>
/// What a <see cref="NotificationRuleTarget"/> points at when the rule is folder- or
/// workflow-scoped.
/// </summary>
public enum NotificationTargetKind
{
    Folder = 0,
    Workflow = 1,
}
