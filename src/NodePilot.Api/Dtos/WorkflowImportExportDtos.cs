using System.Text.Json;

namespace NodePilot.Api.Dtos;

// Export envelope v1. One of Workflow/Workflows is set (single vs bulk).
// "definition" is the parsed workflow object (nodes/edges), not the raw string — so the
// file stays human-readable. On import it is re-serialized into DefinitionJson.
public record WorkflowExportItem(
    string Name, string? Description, JsonElement Definition,
    bool? IsEnabled = null);
public record WorkflowExportEnvelope(
    string Schema,
    int ExportVersion,
    DateTime ExportedAt,
    WorkflowExportItem? Workflow,
    List<WorkflowExportItem>? Workflows);

public record ImportedWorkflowInfo(Guid Id, string Name, string? OriginalName);
public record ImportWorkflowsResponse(
    int Created,
    List<ImportedWorkflowInfo> Workflows,
    List<string> Errors);

public record ScorchImportedWorkflowInfo(
    Guid Id, string Name, string? OriginalName,
    int ActivityCount, int HeuristicCount, int FallbackCount);

public record ScorchImportedVariableInfo(
    string Name, string? OriginalName, bool CreatedNow, bool Skipped, string? SkipReason);

public record ScorchImportResponse(
    int Created,
    List<ScorchImportedWorkflowInfo> Workflows,
    List<ScorchImportedVariableInfo> Variables,
    List<string> Warnings,
    List<string> Errors);
