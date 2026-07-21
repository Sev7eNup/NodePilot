namespace NodePilot.Core.Enums;

/// <summary>
/// Broad grouping for a <c>ISystemAlertSource</c>, used to cluster catalog cards in the alerting UI.
/// Append-only — the names are the persisted/serialized contract.
/// </summary>
public enum SystemAlertCategory
{
    /// <summary>Terminal execution results, long-running / queued-long detection, credential failures.</summary>
    Execution = 0,
    /// <summary>Backlog / pending depth and cancel-rate metrics over the execution queue.</summary>
    Queue = 1,
    /// <summary>Service heartbeat staleness and machine reachability.</summary>
    Health = 2,
    /// <summary>Schedule-missed and workflow-no-recent-success signals.</summary>
    Schedule = 3,
    /// <summary>Credential expiry.</summary>
    Credential = 4,
}

/// <summary>
/// Value type of a system-alert observation field or source parameter. Drives the UI input control and
/// the operator set exposed for the field. Append-only.
/// </summary>
public enum SystemAlertFieldType
{
    String = 0,
    Number = 1,
    Boolean = 2,
    Enum = 3,
    /// <summary>A count of seconds; semantically numeric but rendered as a duration control in the UI.</summary>
    Duration = 4,
}

/// <summary>How far a source's policies may be scoped. Mirrors the <see cref="NotificationScopeKind"/> axis.</summary>
public enum SystemAlertScopeCapability
{
    /// <summary>Global only — observations carry no workflow/folder identity (e.g. backlog depth, cancel-rate).</summary>
    GlobalOnly = 0,
    /// <summary>May be Global, Folders, or Workflows — observations carry workflow/folder identity.</summary>
    WorkflowScoped = 1,
}
