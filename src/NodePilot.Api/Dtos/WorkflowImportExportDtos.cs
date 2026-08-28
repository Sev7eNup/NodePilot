using System.Text.Json;

using System.Text.Json.Serialization;
namespace NodePilot.Api.Dtos;

// Export envelope v1. One of Workflow/Workflows is set (single vs bulk).
// "definition" is the parsed workflow object (nodes/edges), not the raw string — so the
// file stays human-readable. On import it is re-serialized into DefinitionJson.
// MaxConcurrentExecutions travels with the workflow: an omitted limit reads as unlimited on
// the target, which is the unsafe direction for a guard that protects a downstream system.
public record WorkflowExportItem(
    string Name, string? Description, [property: JsonRequired] JsonElement Definition,
    bool? IsEnabled = null, int? MaxConcurrentExecutions = null);
public record WorkflowExportEnvelope(
    string Schema,
    [property: JsonRequired] int ExportVersion,
    [property: JsonRequired] DateTime ExportedAt,
    WorkflowExportItem? Workflow,
    List<WorkflowExportItem>? Workflows);

public record ImportedWorkflowInfo(Guid Id, string Name, string? OriginalName);
public record ImportWorkflowsResponse(
    int Created,
    List<ImportedWorkflowInfo> Workflows,
    List<string> Errors);

/// <param name="FolderPath">
/// Where the workflow landed, as a display path. A SCOrch export carries its own folder tree and
/// the import rebuilds it below the chosen destination, so this is not simply the destination.
/// Null only for a workflow the import did not create.
/// </param>
public record ScorchImportedWorkflowInfo(
    Guid Id, string Name, string? OriginalName,
    int ActivityCount, int HeuristicCount, int FallbackCount,
    string? FolderPath);

/// <param name="FolderPath">Where the variable landed; null when it was skipped.</param>
public record ScorchImportedVariableInfo(
    string Name, string? OriginalName, bool CreatedNow, bool Skipped, string? SkipReason,
    string? FolderPath);

public record ScorchImportResponse(
    int Created,
    List<ScorchImportedWorkflowInfo> Workflows,
    List<ScorchImportedVariableInfo> Variables,
    List<string> Warnings,
    List<string> Errors);
