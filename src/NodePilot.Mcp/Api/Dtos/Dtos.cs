using System.Text.Json;

namespace NodePilot.Mcp.Api.Dtos;

// DTOs duplicated from src/NodePilot.Api/Dtos (no ProjectReference to Api — same convention as the CLI).
// JSON is Web-default (camelCase, case-insensitive); see NodePilotApiClient.JsonOptions.

// ---- Auth ----
public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResponse(string Token, Guid UserId, string Username, string Role);
public sealed record MeResponse(Guid Id, string Username, string Role);

// ---- Workflows ----
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

public sealed record WorkflowContractInput(
    string Name, string Type, bool Required, string? Default, string? Description, bool HasConflict);
public sealed record WorkflowContractOutput(string Name, string Source);
public sealed record WorkflowContractResponse(
    Guid WorkflowId, string WorkflowName,
    bool HasManualTrigger, bool HasReturnData, bool HasMultipleReturnDataNodes,
    IReadOnlyList<WorkflowContractInput> Inputs, IReadOnlyList<WorkflowContractOutput> Outputs);

public sealed record WorkflowVersionInfo(
    int Version, string Name, DateTime CreatedAt, string? CreatedBy, string? ChangeNote, bool IsCurrent);
public sealed record WorkflowVersionDetail(
    int Version, string Name, string? Description, string DefinitionJson,
    DateTime CreatedAt, string? CreatedBy, string? ChangeNote, bool IsCurrent);

public sealed record WorkflowExportItem(string Name, string? Description, JsonElement Definition, bool? IsEnabled = null);
public sealed record WorkflowExportEnvelope(
    string Schema, int ExportVersion, DateTime ExportedAt,
    WorkflowExportItem? Workflow, List<WorkflowExportItem>? Workflows);
public sealed record ImportedWorkflowInfo(Guid Id, string Name, string? OriginalName);
public sealed record ImportWorkflowsResponse(int Created, List<ImportedWorkflowInfo> Workflows, List<string> Errors);

// ---- Executions ----
public sealed record ExecuteWorkflowRequest(
    Dictionary<string, string>? Parameters = null, int? TimeoutSeconds = null, bool Debug = false);

public sealed record FailedStepRef(string StepId, string? StepName);

public sealed record ExecutionResponse(
    Guid Id, Guid WorkflowId, string Status, DateTime StartedAt,
    DateTime? CompletedAt, string? TriggeredBy, string? ErrorMessage,
    string? TraceId = null, string? SpanId = null, string? ReturnData = null,
    string? InputParametersJson = null,
    string? StartedByUsername = null,
    Guid? ParentExecutionId = null,
    string? ParentWorkflowName = null,
    int StepsTotal = 0,
    int StepsCompleted = 0,
    IReadOnlyList<FailedStepRef>? FailedSteps = null);

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

public sealed record CancelAllResponse(int Total, int Signalled);

// Mirrors ExecutionDebugController.ResumeDebugRequest — StepId is REQUIRED by the API
// (parallel branches each have their own paused step), Mode is continue|stepOver|stop.
public sealed record ResumeExecutionRequest(string StepId, string Mode, Dictionary<string, string>? Overrides = null);

// ---- Scheduler ----
public sealed record NextFiresResponse(List<DateTime> Fires, string Summary);

// ---- Step test ----
// ConfigOverride is executable unsaved editor state; the API requires Edit + the caller's lock.
public sealed record StepTestRequest(
    Dictionary<string, string>? MockVariables = null,
    JsonElement? ConfigOverride = null);

public sealed record StepTestResponse(
    bool Success,
    string? Output,
    string? ErrorOutput,
    Dictionary<string, string> OutputParameters,
    double DurationMs,
    string? ErrorMessage);

public sealed record StepTestContextVariable(string Key, string Origin, string Source, string? Value);

public sealed record StepTestContextResponse(
    Guid? ExecutionId, DateTime? ExecutedAt, string? Status,
    IReadOnlyList<StepTestContextVariable> Variables);

public sealed record StepTestContextRunInfo(
    Guid ExecutionId, DateTime StartedAt, string Status, string? TriggeredBy, bool StepRan);

// ---- Telemetry ----
public sealed record NodeCoverageStats(
    string StepId, int ExecutedCount, int FailedCount, int SkippedCount,
    DateTime? LastExecutedAt, DateTime? LastSucceededAt, DateTime? LastFailedAt);

public sealed record WorkflowCoverageResponse(
    Guid WorkflowId, int WindowDays, int TotalExecutions,
    DateTime? OldestExecutionInWindow, IReadOnlyList<NodeCoverageStats> Nodes);

public sealed record StepHealthEntry(string Status, DateTime? StartedAt);

public sealed record StepStats(
    int TotalRuns, int FailedRuns, double FailureRate,
    long AvgDurationMs, long P95DurationMs, long LastDurationMs);

// ---- Audit ----
public sealed record AuditEntryResponse(
    Guid Id, DateTime Timestamp, Guid? UserId, string? Username, string Action,
    string? ResourceType, Guid? ResourceId, string? Details, string? IpAddress);
public sealed record AuditCursor(DateTime Timestamp, Guid Id);
public sealed record AuditPageResponse(IReadOnlyList<AuditEntryResponse> Items, AuditCursor? NextCursor);

// ---- Machines ----
public sealed record MachineResponse(
    Guid Id, string Name, string Hostname, int WinRmPort, bool UseSsl,
    Guid? DefaultCredentialId, string? Tags, DateTime? LastConnectivityCheck, bool IsReachable,
    int UsedByWorkflowCount, int RecentStepCount, int RecentFailedStepCount, int ActiveRunCount);
public sealed record CreateMachineRequest(
    string Name, string Hostname, int WinRmPort = 5985, bool UseSsl = false,
    Guid? DefaultCredentialId = null, string? Tags = null);
public sealed record UpdateMachineRequest(
    string Name, string Hostname, int WinRmPort, bool UseSsl, Guid? DefaultCredentialId, string? Tags);
public sealed record TestConnectionRequest(Guid? CredentialId = null);
public sealed record TestConnectionResponse(bool Success, string? ComputerName, string? Error, string? CredentialUsed);

// ---- Credentials (password never returned) ----
public sealed record CredentialResponse(Guid Id, string Name, string Username, string? Domain, DateTime? ExpiresAt = null);
public sealed record CreateCredentialRequest(string Name, string Username, string Password, string? Domain, DateTime? ExpiresAt = null);
public sealed record UpdateCredentialRequest(string Name, string Username, string? Password, string? Domain, DateTime? ExpiresAt = null);

// ---- Global variables (secret values masked server-side) ----
public sealed record GlobalVariableResponse(
    Guid Id, string Name, string? Value, bool IsSecret, string? Description,
    Guid FolderId, DateTime CreatedAt, DateTime UpdatedAt, string? UpdatedBy);
public sealed record CreateGlobalVariableRequest(string Name, string Value, bool IsSecret, string? Description, Guid? FolderId = null);
public sealed record UpdateGlobalVariableRequest(string Name, string? Value, bool IsSecret, string? Description, Guid? FolderId = null);
public sealed record MoveGlobalVariableRequest(Guid FolderId);

// ---- Global variable folders (organizational tree; cosmetic) ----
public sealed record GlobalVariableFolderResponse(
    Guid Id, Guid? ParentFolderId, string Name, string Path, int Depth,
    DateTime CreatedAt, Guid? CreatedByUserId, int VariableCount);
public sealed record CreateGlobalVariableFolderRequest(Guid? ParentFolderId, string Name);
public sealed record UpdateGlobalVariableFolderRequest(string Name);
public sealed record MoveGlobalVariableFolderRequest(Guid? NewParentFolderId);
