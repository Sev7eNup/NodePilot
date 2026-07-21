namespace NodePilot.Cli.Api.Dtos;

// CLI-side mirrors of the API DTOs added in src/NodePilot.Api/Dtos/WorkflowDtos.cs.
// Per CLAUDE.md convention these are duplicated (no ProjectReference on NodePilot.Api)
// so the CLI version stays independently releasable.

// ---- Machines ----------------------------------------------------------------

public sealed record MachineResponse(
    Guid Id, string Name, string Hostname, int WinRmPort, bool UseSsl,
    Guid? DefaultCredentialId, string? Tags, DateTime? LastConnectivityCheck, bool IsReachable,
    int UsedByWorkflowCount = 0,
    int RecentStepCount = 0,
    int RecentFailedStepCount = 0,
    int ActiveRunCount = 0);

public sealed record CreateMachineRequest(
    string Name, string Hostname, int WinRmPort = 5985, bool UseSsl = false,
    Guid? DefaultCredentialId = null, string? Tags = null);

public sealed record UpdateMachineRequest(
    string Name, string Hostname, int WinRmPort, bool UseSsl,
    Guid? DefaultCredentialId, string? Tags);

public sealed record TestConnectionRequest(Guid? CredentialId);

// `success`/`computerName`/`error`/`credentialUsed` — server returns an anonymous object,
// here typed for clean rendering.
public sealed record TestConnectionResponse(
    bool Success, string? ComputerName, string? Error, string? CredentialUsed);

// ---- Credentials -------------------------------------------------------------

public sealed record CredentialResponse(Guid Id, string Name, string Username, string? Domain, DateTime? ExpiresAt = null);
public sealed record CreateCredentialRequest(string Name, string Username, string Password, string? Domain, DateTime? ExpiresAt = null);
public sealed record UpdateCredentialRequest(string Name, string Username, string? Password, string? Domain, DateTime? ExpiresAt = null);

// ---- Global Variables --------------------------------------------------------

public sealed record GlobalVariableResponse(
    Guid Id, string Name, string? Value, bool IsSecret, string? Description,
    Guid FolderId, DateTime CreatedAt, DateTime UpdatedAt, string? UpdatedBy);

public sealed record CreateGlobalVariableRequest(string Name, string Value, bool IsSecret, string? Description, Guid? FolderId = null);
public sealed record UpdateGlobalVariableRequest(string Name, string? Value, bool IsSecret, string? Description, Guid? FolderId = null);
public sealed record MoveGlobalVariableRequest(Guid FolderId);

// Used by `np globals export` output and `np globals import` input.
// Intentionally omits Id/timestamps — only the three mutable fields matter for round-trips.
// Secret values from export are "***"; replace before re-importing.
public sealed record ImportableGlobalVariable(string Name, string? Value, bool IsSecret, string? Description);

// ---- Global Variable Folders -------------------------------------------------

public sealed record GlobalVariableFolderResponse(
    Guid Id, Guid? ParentFolderId, string Name, string Path, int Depth,
    DateTime CreatedAt, Guid? CreatedByUserId, int VariableCount);

public sealed record CreateGlobalVariableFolderRequest(Guid? ParentFolderId, string Name);
public sealed record UpdateGlobalVariableFolderRequest(string Name);
public sealed record MoveGlobalVariableFolderRequest(Guid? NewParentFolderId);

// ---- Maintenance Windows -----------------------------------------------------

public sealed record MaintenanceWindowTargetDto(string TargetKind, Guid TargetId);

public sealed record MaintenanceWindowResponse(
    Guid Id, string Name, string? Description, bool IsEnabled,
    string Mode, string ScopeKind, string Recurrence,
    DateTime? OneTimeStartUtc, DateTime? OneTimeEndUtc,
    int WeeklyDaysMask, int? WeeklyStartMinuteOfDay, int? WeeklyEndMinuteOfDay,
    string? CronExpression, int? DurationMinutes, string TimeZoneId,
    List<MaintenanceWindowTargetDto> Targets,
    DateTime CreatedAt, DateTime UpdatedAt, string? UpdatedBy);

public sealed record CreateMaintenanceWindowRequest(
    string Name, string? Description, bool IsEnabled, string Mode, string ScopeKind, string Recurrence,
    DateTime? OneTimeStartUtc, DateTime? OneTimeEndUtc, int WeeklyDaysMask, int? WeeklyStartMinuteOfDay,
    int? WeeklyEndMinuteOfDay, string? CronExpression, int? DurationMinutes, string? TimeZoneId,
    List<MaintenanceWindowTargetDto>? Targets);

public sealed record UpdateMaintenanceWindowRequest(
    string Name, string? Description, bool IsEnabled, string Mode, string ScopeKind, string Recurrence,
    DateTime? OneTimeStartUtc, DateTime? OneTimeEndUtc, int WeeklyDaysMask, int? WeeklyStartMinuteOfDay,
    int? WeeklyEndMinuteOfDay, string? CronExpression, int? DurationMinutes, string? TimeZoneId,
    List<MaintenanceWindowTargetDto>? Targets);

// ---- Users (Admin-only) ------------------------------------------------------

public sealed record UserResponse(Guid Id, string Username, string Role, bool IsActive, DateTime CreatedAt);
public sealed record CreateUserRequest(string Username, string Password, string Role);
public sealed record UpdateUserRequest(string? Role, bool? IsActive, string? Password);

// ---- Step Stats / Health -----------------------------------------------------

public sealed record StepHealthEntry(string Status, DateTime? StartedAt);

public sealed record StepStats(
    int TotalRuns, int FailedRuns, double FailureRate,
    long AvgDurationMs, long P95DurationMs, long LastDurationMs);

// ---- Dashboard ---------------------------------------------------------------

public sealed record ExecutionCounts(int Total, int Succeeded, int Failed, int Running, int Cancelled);
public sealed record HourBucket(DateTime HourStart, int Succeeded, int Failed, int Cancelled);
public sealed record TopWorkflow(Guid Id, string Name, int RunCount, int SuccessCount, int FailCount);
public sealed record RunningExecutionInfo(
    Guid Id, Guid WorkflowId, string WorkflowName, string Status, DateTime StartedAt, string? TriggeredBy);
public sealed record RecentExecutionInfo(
    Guid Id, Guid WorkflowId, string WorkflowName, string Status,
    DateTime StartedAt, DateTime? CompletedAt, long? DurationMs, string? TriggeredBy);
public sealed record ArmedTriggerInfo(Guid WorkflowId, string WorkflowName, List<string> TriggerTypes);

public sealed record DashboardStats(
    int WorkflowsTotal, int WorkflowsEnabled,
    int MachinesTotal, int MachinesReachable,
    int ExecutionsTotal,
    ExecutionCounts Last24h,
    List<HourBucket> Last24hBuckets,
    List<TopWorkflow> TopWorkflows,
    List<RunningExecutionInfo> Running,
    List<RecentExecutionInfo> Recent,
    List<ArmedTriggerInfo> ArmedTriggers);

// ---- Observability -----------------------------------------------------------

public sealed record TelemetryPanel(string Key, string Title, string Unit, double? Value, string? Error);
public sealed record TelemetrySummaryResponse(bool Available, List<TelemetryPanel> Panels);

// ---- Debug Resume / Paused ---------------------------------------------------

public sealed record ResumeDebugRequest(string StepId, string Mode, Dictionary<string, string>? Overrides);

// ---- Scorch import (XML) -----------------------------------------------------

public sealed record ScorchImportedWorkflowInfo(
    Guid Id, string Name, string? OriginalName,
    int ActivityCount, int HeuristicCount, int FallbackCount);

public sealed record ScorchImportedVariableInfo(
    string Name, string? OriginalName, bool CreatedNow, bool Skipped, string? SkipReason);

public sealed record ScorchImportResponse(
    int Created,
    List<ScorchImportedWorkflowInfo> Workflows,
    List<ScorchImportedVariableInfo> Variables,
    List<string> Warnings,
    List<string> Errors);
