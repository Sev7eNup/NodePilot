using NodePilot.Core.Activities;

namespace NodePilot.Api.Dtos;

/// <summary>Full custom-activity detail incl. the script — returned to authors (Admin/Operator) for editing.</summary>
public sealed record CustomActivityResponse(
    Guid Id, string Key, string Type, string Name, string? Description, string Icon, string? Color,
    string ScriptTemplate, string Engine, bool RunsRemote, bool Isolated, int? MemoryLimitMb,
    int? MaxProcesses, int? DefaultTimeoutSeconds, string? SuccessExitCodes,
    IReadOnlyList<CustomActivityInputParameter> Inputs, IReadOnlyList<CustomActivityOutputParameter> Outputs,
    bool IsEnabled, int Version, Guid ConcurrencyToken,
    DateTime CreatedAt, DateTime UpdatedAt, string? CreatedBy, string? UpdatedBy, string? ChangeNote);

/// <summary>
/// Lightweight palette/catalog entry — NO script (fetched by every role to drive the designer).
/// <see cref="Timeout"/> is "always" since custom activities always run a script (UI timeout gating).
/// </summary>
public sealed record CustomActivityCatalogEntry(
    Guid Id, string Key, string Type, string Name, string? Description, string Icon, string? Color,
    bool RunsRemote, string Timeout,
    IReadOnlyList<CustomActivityInputParameter> Inputs, IReadOnlyList<CustomActivityOutputParameter> Outputs,
    bool IsEnabled, int Version);

public sealed record CreateCustomActivityRequest(
    string Key, string Name, string? Description, string? Icon, string? Color,
    string ScriptTemplate, string? Engine, bool RunsRemote, bool Isolated, int? MemoryLimitMb,
    int? MaxProcesses, int? DefaultTimeoutSeconds, string? SuccessExitCodes,
    IReadOnlyList<CustomActivityInputParameter>? Inputs, IReadOnlyList<CustomActivityOutputParameter>? Outputs);

public sealed record UpdateCustomActivityRequest(
    string Name, string? Description, string? Icon, string? Color,
    string ScriptTemplate, string? Engine, bool RunsRemote, bool Isolated, int? MemoryLimitMb,
    int? MaxProcesses, int? DefaultTimeoutSeconds, string? SuccessExitCodes,
    IReadOnlyList<CustomActivityInputParameter>? Inputs, IReadOnlyList<CustomActivityOutputParameter>? Outputs,
    Guid ConcurrencyToken, string? ChangeNote);

public sealed record CustomActivityVersionResponse(
    int Version, string Name, string? Description, string Icon, string? Color, string ScriptTemplate,
    string Engine, bool RunsRemote, bool Isolated, int? MemoryLimitMb, int? MaxProcesses,
    int? DefaultTimeoutSeconds, string? SuccessExitCodes,
    IReadOnlyList<CustomActivityInputParameter> Inputs, IReadOnlyList<CustomActivityOutputParameter> Outputs,
    DateTime CreatedAt, string? CreatedBy, string? ChangeNote);

public sealed record CustomActivityLintWarning(string Rule, string Message);

/// <summary>Create/Update response — carries the saved definition plus any non-blocking script lint warnings.</summary>
public sealed record CustomActivitySaveResponse(
    CustomActivityResponse Definition, IReadOnlyList<CustomActivityLintWarning> Warnings);

public sealed record CustomActivityExportItem(
    string Key, string Name, string? Description, string Icon, string? Color, string ScriptTemplate,
    string Engine, bool RunsRemote, bool Isolated, int? MemoryLimitMb, int? MaxProcesses,
    int? DefaultTimeoutSeconds, string? SuccessExitCodes,
    IReadOnlyList<CustomActivityInputParameter> Inputs, IReadOnlyList<CustomActivityOutputParameter> Outputs);

public sealed record CustomActivityExportEnvelope(
    string Schema, int ExportVersion, DateTime ExportedAt, IReadOnlyList<CustomActivityExportItem> Items);
