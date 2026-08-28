namespace NodePilot.Core.Enums;

/// <summary>
/// Broad grouping for an <c>ISystemAlertSource</c>; clusters catalog cards in the alerting UI.
/// Append-only: the names are the persisted contract.
/// </summary>
public enum SystemAlertCategory
{
    /// <summary>Terminal execution results, long-running steps, credential failures.</summary>
    Execution = 0,
    /// <summary>Backlog / pending depth and cancel-rate metrics over the execution queue.</summary>
    Queue = 1,
    /// <summary>Service heartbeat staleness and machine reachability.</summary>
    Health = 2,
    /// <summary>Schedule-missed and workflow-no-recent-success signals.</summary>
    Schedule = 3,
    /// <summary>Credential expiry.</summary>
    Credential = 4,
    /// <summary>Audit-log events: authentication, privilege and configuration changes.</summary>
    Security = 5,
}

/// <summary>
/// Value type of a system-alert observation field or source parameter. Drives the UI input
/// control and the operator set exposed for the field. Append-only.
/// </summary>
public enum SystemAlertFieldType
{
    String = 0,
    Number = 1,
    Boolean = 2,
    Enum = 3,
    /// <summary>Seconds; numeric, but rendered as a duration control in the UI.</summary>
    Duration = 4,
}

/// <summary>
/// How far a source's policies may be scoped. Mirrors <see cref="NotificationScopeKind"/>.
/// </summary>
public enum SystemAlertScopeCapability
{
    /// <summary>Global only: observations carry no workflow or folder identity.</summary>
    GlobalOnly = 0,
    /// <summary>Global, Folders or Workflows: observations carry a workflow identity.</summary>
    WorkflowScoped = 1,
}
