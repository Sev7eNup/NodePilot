using System.Text.Json;
using NodePilot.Core.WorkflowDefinitions;

namespace NodePilot.Core.Models;

/// <summary>
/// Computes the denormalized <see cref="Workflow.TriggerTypesJson"/> and
/// <see cref="Workflow.ActivityCount"/> columns from a workflow's <c>DefinitionJson</c>.
/// Shared so the controller write paths and the startup backfill agree on the format.
/// </summary>
public static class WorkflowMetadata
{
    public static void PopulateComputedColumns(Workflow workflow)
    {
        var (count, triggerTypesJson) = Compute(workflow.DefinitionJson);
        workflow.ActivityCount = count;
        workflow.TriggerTypesJson = triggerTypesJson;
    }

    public static (int ActivityCount, string TriggerTypesJson) Compute(string definitionJson)
    {
        if (!WorkflowDefinitionDocument.TryParse(definitionJson, out var definition) || definition is null)
            return (0, "[]");

        return (
            definition.Metadata.ActivityCount,
            JsonSerializer.Serialize(definition.Metadata.TriggerTypes));
    }
}
