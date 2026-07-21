using System.Text.Json;

namespace NodePilot.Cli.Api.Dtos;

public sealed record LastExecutionInfo(
    Guid Id, string Status, DateTime StartedAt, DateTime? CompletedAt, long? DurationMs);

public sealed record WorkflowResponse(
    Guid Id,
    string Name,
    string? Description,
    string DefinitionJson,
    int Version,
    bool IsEnabled,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? CreatedBy,
    string? UpdatedBy,
    int ActivityCount,
    List<string> TriggerTypes,
    LastExecutionInfo? LastExecution,
    int SuccessCount,
    int TotalCount,
    double? AvgDurationMs,
    Guid? CheckedOutByUserId,
    string? CheckedOutByUserName,
    DateTime? CheckedOutAt);

public sealed record CreateWorkflowRequest(string Name, string? Description, string DefinitionJson);
public sealed record UpdateWorkflowRequest(string Name, string? Description, string DefinitionJson);
public sealed record PublishWorkflowRequest(string Name, string? Description, string DefinitionJson);
public sealed record RollbackRequest(string? Reason);

public sealed record ExecuteWorkflowRequest(
    Dictionary<string, string>? Parameters = null,
    int? TimeoutSeconds = null,
    bool Debug = false);

public sealed record ExecutionResponse(
    Guid Id, Guid WorkflowId, string Status, DateTime StartedAt,
    DateTime? CompletedAt, string? TriggeredBy, string? ErrorMessage,
    string? TraceId = null, string? SpanId = null, string? ReturnData = null,
    string? InputParametersJson = null,
    // Mirrors the API's triage fields. Not shown by the CLI renderers (Renderers.Executions /
    // ExecutionDetail) yet — kept here purely additively so CLI deserialization stays in sync
    // with the larger API DTO and doesn't break as fields get added there.
    string? StartedByUsername = null,
    Guid? ParentExecutionId = null,
    string? ParentWorkflowName = null,
    int StepsTotal = 0,
    int StepsCompleted = 0,
    IReadOnlyList<FailedStepRef>? FailedSteps = null);

public sealed record FailedStepRef(string StepId, string? StepName);

public sealed record StepExecutionResponse(
    Guid Id, string StepId, string? StepName, string StepType, string? TargetMachine,
    string Status, DateTime? StartedAt, DateTime? CompletedAt,
    string? Output, string? ErrorOutput,
    int AttemptCount,
    DateTime? PausedAt,
    string? VariablesSnapshot,
    string? TraceOutput,
    string? OutputParametersJson = null,
    string? OutputVariable = null,
    string? CustomActivityKey = null,
    int? CustomActivityVersion = null,
    string? CustomActivityHash = null);

public sealed record WorkflowVersionInfo(
    int Version, string Name, DateTime CreatedAt, string? CreatedBy, string? ChangeNote, bool IsCurrent);

public sealed record WorkflowVersionDetail(
    int Version, string Name, string? Description, string DefinitionJson,
    DateTime CreatedAt, string? CreatedBy, string? ChangeNote, bool IsCurrent);

public sealed record CancelAllResponse(int Total, int Signalled);

public sealed record AuditEntryResponse(
    Guid Id, DateTime Timestamp, Guid? UserId, string? Username, string Action,
    string? ResourceType, Guid? ResourceId, string? Details, string? IpAddress);

public sealed record AuditCursor(DateTime Timestamp, Guid Id);

public sealed record AuditPageResponse(IReadOnlyList<AuditEntryResponse> Items, AuditCursor? NextCursor);

public sealed record NextFiresResponse(List<DateTime> Fires, string Summary);

public sealed record WorkflowExportItem(
    string Name, string? Description, JsonElement Definition,
    bool? IsEnabled = null);

public sealed record WorkflowExportEnvelope(
    string Schema,
    int ExportVersion,
    DateTime ExportedAt,
    WorkflowExportItem? Workflow,
    List<WorkflowExportItem>? Workflows);

public sealed record ImportedWorkflowInfo(Guid Id, string Name, string? OriginalName);
public sealed record ImportWorkflowsResponse(int Created, List<ImportedWorkflowInfo> Workflows, List<string> Errors);
