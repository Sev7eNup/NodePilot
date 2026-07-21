namespace NodePilot.Api.Dtos;

/// <summary>
/// Snapshot for the live-ops Mission-Control view at <c>/operations</c>. RBAC-folder-scoped: only
/// workflows the caller can read appear as nodes, and call edges resolve only against that
/// scoped set (so a reference to a workflow outside the caller's folders shows as unresolved,
/// never leaking its existence). Live status deltas arrive separately over the SignalR
/// <c>ops-feed</c> group; this is the initial paint: nodes, static call topology, currently
/// running executions and the recently finished ones for the live timeline.
/// </summary>
public record OperationsGraphDto(
    IReadOnlyList<OpsNode> Nodes,
    IReadOnlyList<OpsEdge> Edges,
    IReadOnlyList<OpsRunningExecution> Running,
    IReadOnlyList<OpsRecentExecution> Recent,
    OpsCapabilities Capabilities);

/// <param name="RunningCount">Live count of Running/Pending executions at snapshot time.</param>
/// <param name="LastStatus">Status of the most recent execution (from WorkflowStats), or null if never run.</param>
/// <param name="CallFrequency">Run count in the stats window — drives node-size-by-throughput. Null if no stats row.</param>
public record OpsNode(
    Guid WorkflowId,
    string Name,
    Guid FolderId,
    string FolderPath,
    bool IsEnabled,
    int RunningCount,
    string? LastStatus,
    int? CallFrequency);

/// <param name="Target">Resolved target workflow id; null for dynamic/unresolved/ambiguous refs.</param>
/// <param name="Kind"><c>startWorkflow</c> or <c>forEach</c>.</param>
/// <param name="RefStatus"><c>Resolved</c> | <c>Dynamic</c> | <c>Unresolved</c> | <c>Ambiguous</c>.</param>
/// <param name="RawRef">Original reference string — shown for non-resolved (dynamic/unresolved/ambiguous) edges.</param>
public record OpsEdge(
    string Id,
    Guid Source,
    Guid? Target,
    string Kind,
    string RefStatus,
    string RawRef,
    int CallCount);

/// <param name="ParentExecutionId">Parent run for sub-workflow executions (startWorkflow/forEach)
/// — lets the timeline draw call connectors between parent and child bars.</param>
public record OpsRunningExecution(
    Guid ExecutionId,
    Guid WorkflowId,
    string Status,
    DateTime StartedAt,
    Guid? ParentExecutionId);

/// <summary>
/// Terminal execution completed within the recent window (30 min, newest 200 win on very busy
/// systems — bars age out of the timeline before the cap matters). Slim on purpose: rich details
/// (error, triggeredBy, parent) come from <c>GET /api/executions/{id}</c> on drill-down.
/// </summary>
/// <param name="ParentExecutionId">Parent run for sub-workflow executions — see <see cref="OpsRunningExecution"/>.</param>
public record OpsRecentExecution(
    Guid ExecutionId,
    Guid WorkflowId,
    string Status,
    DateTime StartedAt,
    DateTime CompletedAt,
    Guid? ParentExecutionId);

/// <summary>What the caller may do from the console. Cancel re-checks per-workflow RBAC server-side.</summary>
public record OpsCapabilities(bool CanCancel);
