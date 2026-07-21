using System.Text.Json;

namespace NodePilot.Core.WorkflowDefinitions;

public sealed record WorkflowContractInputDefinition(
    string Name,
    string Type,
    bool Required,
    string? Default,
    string? Description,
    bool HasConflict);

public sealed record WorkflowContractOutputDefinition(string Name, string Source);

public sealed record WorkflowContractDefinition(
    bool HasManualTrigger,
    bool HasReturnData,
    bool HasMultipleReturnDataNodes,
    IReadOnlyList<WorkflowContractInputDefinition> Inputs,
    IReadOnlyList<WorkflowContractOutputDefinition> Outputs);

public static class WorkflowDefinitionContractDeriver
{
    private static readonly HashSet<string> ReservedOutputKeys = new(StringComparer.Ordinal)
    {
        "__executionId", "__status", "__workflowId", "__workflowName",
    };

    public static WorkflowContractDefinition Derive(string? definitionJson)
    {
        if (!WorkflowDefinitionDocument.TryParse(definitionJson, out var definition) || definition is null)
            return Empty();

        var inputs = new Dictionary<string, WorkflowContractInputDefinition>(StringComparer.Ordinal);
        var outputKeys = new HashSet<string>(StringComparer.Ordinal);
        var manualTriggerCount = 0;
        var returnDataCount = 0;

        foreach (var node in definition.Nodes)
        {
            if (definition.DisabledNodeIds.Contains(node.Id))
                continue;

            switch (node.Type)
            {
                case "manualTrigger":
                    manualTriggerCount++;
                    CollectInputs(node.Data.Config, inputs);
                    break;
                case "returnData":
                    returnDataCount++;
                    CollectOutputs(node.Data.Config, outputKeys);
                    break;
            }
        }

        var outputs = new List<WorkflowContractOutputDefinition>();
        outputs.AddRange(SystemOutputs());
        foreach (var key in outputKeys.OrderBy(k => k, StringComparer.Ordinal))
        {
            outputs.Add(new WorkflowContractOutputDefinition(
                key,
                Source: returnDataCount > 1 ? "multiple" : "single"));
        }

        return new WorkflowContractDefinition(
            HasManualTrigger: manualTriggerCount > 0,
            HasReturnData: returnDataCount > 0,
            HasMultipleReturnDataNodes: returnDataCount > 1,
            Inputs: inputs.Values.ToList(),
            Outputs: outputs);
    }

    private static WorkflowContractDefinition Empty() =>
        new(
            HasManualTrigger: false,
            HasReturnData: false,
            HasMultipleReturnDataNodes: false,
            Inputs: Array.Empty<WorkflowContractInputDefinition>(),
            Outputs: SystemOutputs());

    private static IReadOnlyList<WorkflowContractOutputDefinition> SystemOutputs() => new[]
    {
        new WorkflowContractOutputDefinition("__executionId", "system"),
        new WorkflowContractOutputDefinition("__status", "system"),
        new WorkflowContractOutputDefinition("__workflowId", "system"),
        new WorkflowContractOutputDefinition("__workflowName", "system"),
    };

    private static void CollectInputs(JsonElement config, Dictionary<string, WorkflowContractInputDefinition> sink)
    {
        if (config.ValueKind != JsonValueKind.Object)
            return;
        if (!config.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Array)
            return;

        foreach (var parameter in parameters.EnumerateArray())
        {
            if (parameter.ValueKind != JsonValueKind.Object)
                continue;

            var name = (parameter.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null)?.Trim();
            if (string.IsNullOrEmpty(name))
                continue;

            var type = (parameter.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null)?.Trim();
            if (string.IsNullOrEmpty(type))
                type = "string";

            var required = parameter.TryGetProperty("required", out var requiredElement)
                           && requiredElement.ValueKind == JsonValueKind.True;

            string? defaultValue = null;
            if (parameter.TryGetProperty("default", out var defaultElement))
            {
                defaultValue = defaultElement.ValueKind switch
                {
                    JsonValueKind.String => defaultElement.GetString(),
                    JsonValueKind.Null => null,
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Number => defaultElement.GetRawText(),
                    _ => null,
                };
            }

            var description = parameter.TryGetProperty("description", out var descriptionElement)
                              && descriptionElement.ValueKind == JsonValueKind.String
                ? descriptionElement.GetString()
                : null;

            if (sink.TryGetValue(name, out var existing))
            {
                var conflict = existing.HasConflict
                               || !string.Equals(existing.Type, type, StringComparison.Ordinal)
                               || !string.Equals(existing.Default, defaultValue, StringComparison.Ordinal);
                sink[name] = existing with
                {
                    Required = existing.Required || required,
                    HasConflict = conflict,
                };
                continue;
            }

            sink[name] = new WorkflowContractInputDefinition(
                name,
                type,
                required,
                defaultValue,
                description,
                HasConflict: false);
        }
    }

    private static void CollectOutputs(JsonElement config, HashSet<string> sink)
    {
        if (config.ValueKind != JsonValueKind.Object)
            return;
        if (!config.TryGetProperty("data", out var dataObject) || dataObject.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in dataObject.EnumerateObject())
        {
            if (string.IsNullOrEmpty(property.Name))
                continue;
            if (ReservedOutputKeys.Contains(property.Name))
                continue;

            sink.Add(property.Name);
        }
    }
}
