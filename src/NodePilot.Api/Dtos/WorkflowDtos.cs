namespace NodePilot.Api.Dtos;

// Workflow-domain DTOs only (CRUD, contract, versions, capabilities, publish).
// Sibling resources live in their own per-resource files (MachineDtos, CredentialDtos,
// UserDtos, AuthDtos, GlobalVariableDtos, AuditDtos, ExecutionDtos, WorkflowTelemetryDtos,
// StepTestDtos, WorkflowImportExportDtos, DashboardDtos) — this file was historically a
// multi-resource catch-all and was decomposed in the 2026-07 coherence cleanup.

/// <summary>
/// Body for <c>POST /api/workflows</c>. <see cref="FolderId"/> is the optional target shared
/// folder; null means "create in Root". The controller verifies the caller has Edit on the
/// chosen folder before persisting.
/// </summary>
public record CreateWorkflowRequest(string Name, string? Description, string DefinitionJson, Guid? FolderId = null);
public record UpdateWorkflowRequest(string Name, string? Description, string DefinitionJson);

/// <summary>
/// One declared input parameter in a workflow's <c>manualTrigger.parameters</c> array.
///
/// <para><c>HasConflict</c> is set when the same parameter name was declared in multiple
/// non-disabled <c>manualTrigger</c> nodes with diverging <c>type</c> or <c>default</c>
/// (variant B from the spec — strict-merge, render with warning rather than hard-fail).
/// The fields surfaced are the values from the *first* trigger node encountered; the UI
/// uses <c>HasConflict</c> to render a warning so the author can reconcile.</para>
/// </summary>
public record WorkflowContractInput(
    string Name,
    string Type,
    bool Required,
    string? Default,
    string? Description,
    bool HasConflict);

/// <summary>
/// One key surfaced as available downstream after the parent's <c>startWorkflow</c> step.
///
/// <para><c>Source</c>:</para>
/// <para>- <c>"system"</c>: always-available metadata (<c>__executionId</c>, <c>__status</c>,
///       <c>__workflowId</c>, <c>__workflowName</c>) injected by the engine, independent of
///       <c>returnData</c>.</para>
/// <para>- <c>"single"</c>: the workflow has exactly one <c>returnData</c>-node and this key
///       comes from it. Reliable.</para>
/// <para>- <c>"multiple"</c>: ≥2 <c>returnData</c>-nodes were found that could each
///       contribute this key. Per-execution, only one <c>returnData</c>-node wins (whichever
///       branch ran), so the key may or may not be populated for any given run. UI renders
///       a warning icon.</para>
/// </summary>
public record WorkflowContractOutput(
    string Name,
    string Source);

public record WorkflowContractResponse(
    Guid WorkflowId,
    string WorkflowName,
    // HasManualTrigger: true iff at least one non-disabled manualTrigger-node was found.
    // False does NOT mean the workflow can't be called as a sub-workflow — startWorkflow
    // can call any enabled workflow. It just means there's no declared input contract,
    // so the UI falls back to the free-form ParameterTable.
    bool HasManualTrigger,
    // HasReturnData: true iff at least one non-disabled returnData-node was found.
    // When false, only the system outputs (__executionId etc.) are downstream-
    // available — no user-defined keys.
    bool HasReturnData,
    // HasMultipleReturnDataNodes: true iff multiple non-disabled returnData-nodes were found.
    // UI uses this as the gate for the per-output "may not be populated this run"-warning.
    bool HasMultipleReturnDataNodes,
    IReadOnlyList<WorkflowContractInput> Inputs,
    IReadOnlyList<WorkflowContractOutput> Outputs);

// Version history — metadata-only list item (no DefinitionJson to keep list responses small).
public record WorkflowVersionInfo(
    int Version, string Name, DateTime CreatedAt, string? CreatedBy, string? ChangeNote, bool IsCurrent);

public record WorkflowVersionDetail(
    int Version, string Name, string? Description, string DefinitionJson,
    DateTime CreatedAt, string? CreatedBy, string? ChangeNote, bool IsCurrent);

public record RollbackRequest(string? Reason);

public record LastExecutionInfo(
    Guid Id, string Status, DateTime StartedAt, DateTime? CompletedAt, long? DurationMs);

public record WorkflowResponse(
    Guid Id, string Name, string? Description, string DefinitionJson,
    int Version, bool IsEnabled, DateTime CreatedAt, DateTime UpdatedAt, string? CreatedBy, string? UpdatedBy)
{
    public int ActivityCount { get; init; }
    public List<string> TriggerTypes { get; init; } = new();
    public LastExecutionInfo? LastExecution { get; init; }
    public int SuccessCount { get; init; }
    public int TotalCount { get; init; }
    public double? AvgDurationMs { get; init; }

    // Edit-Lock surface — null when nobody has the workflow checked out. Username is
    // resolved server-side so the UI doesn't need a separate /users round-trip per row.
    public Guid? CheckedOutByUserId { get; init; }
    public string? CheckedOutByUserName { get; init; }
    public DateTime? CheckedOutAt { get; init; }

    // RBAC: which folder this workflow belongs to + its display path. The UI needs both
    // the id (for filtering/grouping) and the path (for breadcrumb display).
    public Guid FolderId { get; init; }
    public string? FolderPath { get; init; }

    // RBAC: per-row capabilities so the UI doesn't have to infer from the global role.
    // DEFAULT-DENY: any code path that builds a WorkflowResponse without explicitly
    // computing capabilities ships read-only flags. The previous "(true, true, true, true)"
    // default masked bugs where Create/Duplicate/etc. forgot to call the async capability
    // resolver — the UI then briefly showed every action button right after a POST.
    public WorkflowCapabilities Capabilities { get; init; } = new(false, false, false, false, false);
}

/// <summary>RBAC capabilities a caller has on a specific workflow. Lifted into the DTO so
/// the UI can hide buttons it cannot use without a separate /me/permissions roundtrip.
/// <para><b>CanDelete</b> is its own flag because the workflow-DELETE endpoint is Admin-only
/// at the controller level. An Operator with folder-Editor rights has <c>CanEdit=true</c>
/// but <c>CanDelete=false</c>.</para>
/// </summary>
public record WorkflowCapabilities(bool CanRead, bool CanRun, bool CanEdit, bool CanDelete, bool CanAdmin);

/// <summary>
/// Body for <c>POST /api/workflows/{id}/publish</c>. Same shape as <see cref="UpdateWorkflowRequest"/>
/// — publish is "save current draft + enable + unlock" in one atomic transaction, so it
/// needs the full editable surface.
/// </summary>
public record PublishWorkflowRequest(string Name, string? Description, string DefinitionJson);
