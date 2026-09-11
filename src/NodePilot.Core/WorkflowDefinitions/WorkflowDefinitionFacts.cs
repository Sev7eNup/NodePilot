using NodePilot.Core.Operations;

namespace NodePilot.Core.WorkflowDefinitions;

/// <summary>
/// Everything the API derives from a workflow definition without needing the definition itself.
///
/// <para>
/// A definition is unbounded text including every inline script, and several list endpoints used
/// to read and parse all of them on every request just to answer a small question. All of those
/// answers change only when somebody saves a workflow, so they are extracted once here and cached
/// against the workflow's revision.
/// </para>
///
/// <para>
/// Pure and definition-local: the result depends on nothing but the definition passed in. A
/// definition that cannot be parsed yields empty facts rather than an error — a broken definition
/// must not take a list endpoint down with it.
/// </para>
/// </summary>
/// <param name="CallSites">Child-workflow references, for the live-ops call graph.</param>
/// <param name="MachineRefs">
/// Distinct managed-machine ids this workflow targets. A node's target can be a template such as
/// <c>{{globals.targetHost}}</c>; those are skipped, because attributing them to a machine would
/// take the runtime resolver.
/// </param>
/// <param name="HasManualTriggerParameters">
/// True when the workflow's first <c>manualTrigger</c> node declares at least one parameter, i.e.
/// starting it asks the caller for input. Mirrors what the run dialog looks for.
/// </param>
public sealed record WorkflowDefinitionFacts(
    IReadOnlyList<WorkflowCallSite> CallSites,
    IReadOnlyList<Guid> MachineRefs,
    bool HasManualTriggerParameters)
{
    public static WorkflowDefinitionFacts Empty { get; } = new([], [], false);

    public static WorkflowDefinitionFacts Extract(string? definitionJson)
    {
        if (!WorkflowDefinitionDocument.TryParse(definitionJson, out var doc) || doc is null)
            return Empty;

        return new WorkflowDefinitionFacts(
            WorkflowCallGraphBuilder.ExtractCallSites(doc),
            ExtractMachineRefs(doc),
            ExtractHasManualTriggerParameters(doc));
    }

    private static List<Guid> ExtractMachineRefs(WorkflowDefinitionDocument doc)
    {
        var ids = new HashSet<Guid>();
        foreach (var node in doc.Nodes)
        {
            if (Guid.TryParse(node.Data.TargetMachineRaw, out var id) && id != Guid.Empty)
                ids.Add(id);
        }
        return [.. ids];
    }

    private static bool ExtractHasManualTriggerParameters(WorkflowDefinitionDocument doc)
    {
        foreach (var node in doc.Nodes)
        {
            if (!string.Equals(node.Type, "manualTrigger", StringComparison.Ordinal))
                continue;
            // First manual trigger wins, matching the run dialog. A second one is not consulted
            // even if this one declares nothing.
            if (node.Data.Config.ValueKind != System.Text.Json.JsonValueKind.Object)
                return false;
            return node.Data.Config.TryGetProperty("parameters", out var parameters)
                && parameters.ValueKind == System.Text.Json.JsonValueKind.Array
                && parameters.GetArrayLength() > 0;
        }
        return false;
    }
}
