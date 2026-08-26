using System.Text.Json;

namespace NodePilot.Cli.Api.Dtos;

// CLI-side mirrors of API DTOs that the CLI didn't expose yet.
// One file per logical batch to keep WorkflowDtos.cs / ResourceDtos.cs stable.

// ---- Auth methods ------------------------------------------------------------

public sealed record AuthMethodsResponse(bool Local, bool Ldap, bool Windows, string? WindowsEndpoint);

// ---- Workflow Contract ------------------------------------------------------

public sealed record WorkflowContractInput(
    string Name, string Type, bool Required,
    string? Default, string? Description, bool HasConflict);

public sealed record WorkflowContractOutput(string Name, string Source);

public sealed record WorkflowContractResponse(
    Guid WorkflowId,
    string WorkflowName,
    bool HasManualTrigger,
    bool HasReturnData,
    bool HasMultipleReturnDataNodes,
    IReadOnlyList<WorkflowContractInput> Inputs,
    IReadOnlyList<WorkflowContractOutput> Outputs);

// ---- Workflow Coverage ------------------------------------------------------

public sealed record NodeCoverageStats(
    string StepId,
    int ExecutedCount,
    int FailedCount,
    int SkippedCount,
    DateTime? LastExecutedAt,
    DateTime? LastSucceededAt,
    DateTime? LastFailedAt);

public sealed record WorkflowCoverageResponse(
    Guid WorkflowId,
    int WindowDays,
    int TotalExecutions,
    DateTime? OldestExecutionInWindow,
    IReadOnlyList<NodeCoverageStats> Nodes);

// ---- Secrets reencrypt ------------------------------------------------------

public sealed record ReencryptionSkip(Guid Id, string Name, string Reason);

public sealed record ReencryptResult(
    int CredentialsRewritten,
    int CredentialsSkipped,
    IReadOnlyList<ReencryptionSkip> CredentialSkipDetails,
    int GlobalSecretsRewritten,
    int GlobalSecretsSkipped,
    IReadOnlyList<ReencryptionSkip> GlobalSecretSkipDetails,
    int WorkflowVersionsRewritten,
    int WorkflowVersionsSkipped,
    IReadOnlyList<ReencryptionSkip> WorkflowVersionSkipDetails,
    bool PartialSuccess);

// ---- Shared workflow folders (RBAC) -----------------------------------------

public sealed record SharedFolderCapabilities(bool CanRead, bool CanRun, bool CanEdit, bool CanAdmin);

public sealed record SharedFolderResponse(
    Guid Id,
    Guid? ParentFolderId,
    string Name,
    string Path,
    int Depth,
    DateTime CreatedAt,
    Guid? CreatedByUserId,
    int WorkflowCount,
    SharedFolderCapabilities Capabilities);

public sealed record CreateSharedFolderRequest(Guid? ParentFolderId, string Name);
public sealed record UpdateSharedFolderRequest(string Name);
public sealed record MoveSharedFolderRequest(Guid? NewParentFolderId);
public sealed record MoveWorkflowToFolderRequest(Guid TargetFolderId);

public sealed record RecursiveFolderDeleteResponse(int DeletedFolders, int DeletedWorkflows);

public sealed record SharedFolderPermissionResponse(
    Guid Id,
    Guid FolderId,
    string PrincipalType,
    string PrincipalKey,
    string? PrincipalDisplayName,
    string Role,
    DateTime GrantedAt,
    Guid? GrantedByUserId);

public sealed record GrantSharedFolderPermissionRequest(
    string PrincipalType,
    string PrincipalKey,
    string Role);

// ---- Admin Settings ---------------------------------------------------------

public sealed record SettingsStatusResponse(
    string OverridesPath,
    bool RestartRequired,
    DateTimeOffset? RestartRequiredSince,
    IReadOnlyList<string> RestartRequiredFor,
    DateTimeOffset? LastSavedAt,
    string? LastSavedBy);

public sealed record SystemInfoResponse(
    string AppVersion,
    string OverridesPath,
    string DatabaseProvider,
    string? DatabaseHost,
    string SecretsProvider,
    bool ClusterEnabled,
    string ClusterNodeId,
    bool ClusterIsLeader,
    string JwtIssuer,
    string JwtAudience);

// Mirrors Api/Dtos/Settings/EffectiveSizingDto.cs. Under automatic tuning the numbers in the
// Engine/Threading/ExecutionDispatch sections are inert, so `np settings get Engine` reports a
// preset rather than what the process runs on — this is the only honest read of the sizing.
// DesiredManualTuning differs from ManualTuning after a save until the process restarts.
public sealed record EffectiveSizingResponse(
    bool ManualTuning,
    bool DesiredManualTuning,
    int ProcessorCount,
    long? UsableMemoryBytes,
    bool IsDesktop,
    IReadOnlyList<SizedValueResponse> Values);

/// <summary>One resolved knob: configuration key, value in force, and the constraint that produced it.</summary>
public sealed record SizedValueResponse(string Key, int Value, string Bound);

public sealed record SettingsTestProbeResult(
    bool Ok,
    string Message,
    double DurationMs,
    string? ErrorKind);

// Test-probe wrappers carry the section DTO + extras. The CLI does not parse the
// inner DTO — it forwards raw JSON from the user — so we model these as JsonElement
// envelopes to mirror the SmtpTestProbeRequest(SmtpSettingsDto Settings, string? ToAddress)
// + LlmTestProbeRequest(string? ProfileId, LlmProfileProbeDto Settings) shapes without
// duplicating every SmtpSettingsDto/LlmProfileProbeDto field.
public sealed record SmtpTestProbeRequest(JsonElement Settings, string? ToAddress);

// ProfileId is only needed when Settings.apiKey is the "__unchanged__" marker — it names the
// stored profile whose key should be used.
public sealed record LlmTestProbeRequest(string? ProfileId, JsonElement Settings);

// ---- Step Test --------------------------------------------------------------
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

public sealed record StepTestContextVariable(
    string Key,
    string Origin,
    string Source,
    string? Value);

public sealed record StepTestContextResponse(
    Guid? ExecutionId,
    DateTime? ExecutedAt,
    string? Status,
    IReadOnlyList<StepTestContextVariable> Variables);

public sealed record StepTestContextRunInfo(
    Guid ExecutionId,
    DateTime StartedAt,
    string Status,
    string? TriggeredBy,
    bool StepRan);

// ---- DbAdmin SQL console ----------------------------------------------------

public sealed record DbAdminInfo(
    string Provider,
    bool AllowWriteQueries,
    int QueryTimeoutSeconds,
    int QueryMaxRows);

public sealed record DbAdminQueryRequestDto(string Sql, string? Mode);

public sealed record DbAdminQueryColumnDto(string Name, string Type);

public sealed record DbAdminQueryResponseDto(
    List<DbAdminQueryColumnDto> Columns,
    List<List<JsonElement>> Rows,
    int? RowsAffected,
    long DurationMs,
    bool Truncated,
    string Mode);

