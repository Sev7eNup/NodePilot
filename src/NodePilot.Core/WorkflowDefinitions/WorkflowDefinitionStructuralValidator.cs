using System.Text.Json;
using NodePilot.Core.Activities;
using NodePilot.Core.Validation;

namespace NodePilot.Core.WorkflowDefinitions;

public static class WorkflowDefinitionStructuralValidator
{
    // Visual-only node types — no executor, no entry in ActivityCatalog. The designer
    // creates them for documentation/grouping, the engine ignores them during traversal.
    // We still require id + data to be well-formed so they don't corrupt the JSON shape.
    private static readonly HashSet<string> _annotationNodeTypes =
        new(StringComparer.OrdinalIgnoreCase) { "stickyNote", "group" };

    public static WorkflowDefinitionValidationResult Validate(JsonElement definition)
    {
        if (definition.ValueKind != JsonValueKind.Object)
            return WorkflowDefinitionValidationResult.Invalid("root must be a JSON object");

        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        if (definition.TryGetProperty("nodes", out var nodes))
        {
            if (nodes.ValueKind != JsonValueKind.Array)
                return WorkflowDefinitionValidationResult.Invalid("nodes must be an array");

            var index = 0;
            foreach (var node in nodes.EnumerateArray())
            {
                var path = $"nodes[{index}]";
                var nodeResult = ValidateNode(node, path, nodeIds);
                if (!nodeResult.IsValid) return nodeResult;
                index++;
            }
        }

        if (definition.TryGetProperty("edges", out var edges))
        {
            if (edges.ValueKind != JsonValueKind.Array)
                return WorkflowDefinitionValidationResult.Invalid("edges must be an array");

            var edgeIds = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var edge in edges.EnumerateArray())
            {
                var path = $"edges[{index}]";
                var edgeResult = ValidateEdge(edge, path, edgeIds, nodeIds);
                if (!edgeResult.IsValid) return edgeResult;
                index++;
            }
        }

        return WorkflowDefinitionValidationResult.Valid;
    }

    private static WorkflowDefinitionValidationResult ValidateNode(
        JsonElement node,
        string path,
        HashSet<string> nodeIds)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return WorkflowDefinitionValidationResult.Invalid($"{path} must be an object");

        if (!TryGetRequiredString(node, "id", out var id))
            return WorkflowDefinitionValidationResult.Invalid($"{path}.id must be a non-empty string");
        if (!nodeIds.Add(id))
            return WorkflowDefinitionValidationResult.Invalid($"duplicate node id '{id}'");

        if (!node.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return WorkflowDefinitionValidationResult.Invalid($"{path}.data must be an object");

        if (node.TryGetProperty("type", out var typeProp)
            && typeProp.ValueKind == JsonValueKind.String
            && _annotationNodeTypes.Contains(typeProp.GetString() ?? ""))
        {
            if (!ValidateOptionalString(data, "label", $"{path}.data.label", allowNull: true, out var annotationError))
                return WorkflowDefinitionValidationResult.Invalid(annotationError!);
            return WorkflowDefinitionValidationResult.Valid;
        }

        string activityType;
        if (data.TryGetProperty("activityType", out var activityTypeElement))
        {
            if (!IsNonEmptyString(activityTypeElement))
                return WorkflowDefinitionValidationResult.Invalid($"{path}.data.activityType must be a non-empty string");
            activityType = activityTypeElement.GetString()!;
        }
        else if (TryGetRequiredString(node, "type", out var nodeType)
                 && !string.Equals(nodeType, "activity", StringComparison.OrdinalIgnoreCase))
        {
            activityType = nodeType;
        }
        else
        {
            return WorkflowDefinitionValidationResult.Invalid(
                $"{path}.data.activityType is required unless node.type is a concrete activity type");
        }

        // custom:<slug> activities are accepted by grammar here (Core stays DB-free); the executor
        // enforces existence/enabled at run time. Built-in types must be in the static catalog.
        if (NodePilot.Core.Activities.CustomActivityType.IsCustomType(activityType))
        {
            if (!NodePilot.Core.Activities.CustomActivityType.IsValidCustomType(activityType))
                return WorkflowDefinitionValidationResult.Invalid(
                    $"{path}.data.activityType '{activityType}' is not a valid custom activity type (expected custom:<slug>)");
        }
        else if (!ActivityCatalog.ByType.ContainsKey(activityType))
        {
            return WorkflowDefinitionValidationResult.Invalid(
                $"{path}.data.activityType references unknown activity type '{activityType}'");
        }

        if (!ValidateOptionalString(data, "label", $"{path}.data.label", allowNull: true, out var error)
            || !ValidateOptionalString(data, "outputVariable", $"{path}.data.outputVariable", allowNull: true, out error)
            || !ValidateOptionalString(data, "targetMachineId", $"{path}.data.targetMachineId", allowNull: true, out error)
            || !ValidateOptionalString(data, "credentialId", $"{path}.data.credentialId", allowNull: true, out error)
            || !ValidateOptionalString(data, "breakpointCondition", $"{path}.data.breakpointCondition", allowNull: true, out error))
        {
            return WorkflowDefinitionValidationResult.Invalid(error!);
        }

        return WorkflowDefinitionValidationResult.Valid;
    }

    private static WorkflowDefinitionValidationResult ValidateEdge(
        JsonElement edge,
        string path,
        HashSet<string> edgeIds,
        HashSet<string> nodeIds)
    {
        if (edge.ValueKind != JsonValueKind.Object)
            return WorkflowDefinitionValidationResult.Invalid($"{path} must be an object");

        if (!TryGetRequiredString(edge, "id", out var id))
            return WorkflowDefinitionValidationResult.Invalid($"{path}.id must be a non-empty string");
        if (!edgeIds.Add(id))
            return WorkflowDefinitionValidationResult.Invalid($"duplicate edge id '{id}'");

        if (!TryGetRequiredString(edge, "source", out var source))
            return WorkflowDefinitionValidationResult.Invalid($"{path}.source must be a non-empty string");
        if (!TryGetRequiredString(edge, "target", out var target))
            return WorkflowDefinitionValidationResult.Invalid($"{path}.target must be a non-empty string");

        if (!nodeIds.Contains(source))
            return WorkflowDefinitionValidationResult.Invalid($"{path}.source references unknown node '{source}'");
        if (!nodeIds.Contains(target))
            return WorkflowDefinitionValidationResult.Invalid($"{path}.target references unknown node '{target}'");

        if (edge.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && !ValidateOptionalString(data, "label", $"{path}.data.label", allowNull: true, out var error))
        {
            return WorkflowDefinitionValidationResult.Invalid(error!);
        }

        return WorkflowDefinitionValidationResult.Valid;
    }

    private static bool TryGetRequiredString(JsonElement obj, string propertyName, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return false;

        value = property.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool IsNonEmptyString(JsonElement element)
        => element.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(element.GetString());

    private static bool ValidateOptionalString(
        JsonElement obj,
        string propertyName,
        string path,
        bool allowNull,
        out string? error)
    {
        error = null;
        if (!obj.TryGetProperty(propertyName, out var property))
            return true;

        if (property.ValueKind == JsonValueKind.String
            || (allowNull && property.ValueKind == JsonValueKind.Null))
        {
            return true;
        }

        error = $"{path} must be a string";
        return false;
    }
}
