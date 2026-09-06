using System.Text.Json;

namespace NodePilot.Core.WorkflowDefinitions;

/// <summary>
/// Save-time gate for edge conditions. Mirrors what the engine's <c>ConditionEvaluator</c>
/// accepts, so a definition that saves also evaluates: node types, group and comparison
/// operators, operand shapes, and that every referenced step is a node id or an output-variable
/// alias of this workflow. Returns the first problem as a message with its JSON path, or null.
/// </summary>
public static class EdgeConditionValidator
{
    private const int MaxDepth = 20;
    private const int MaxNodes = 200;

    private static readonly HashSet<string> UnaryOps = ["isEmpty", "isNotEmpty", "isTrue", "isFalse"];
    private static readonly HashSet<string> BinaryOps =
        ["==", "!=", "<", ">", "<=", ">=", "contains", "startsWith", "endsWith", "matches"];
    private static readonly HashSet<string> StepFields = ["output", "error", "success", "param"];

    /// <summary>Validates the legacy shortcut <c>&lt;stepId&gt;.success|failed</c>.</summary>
    public static string? ValidateLegacy(
        string condition, string path, IReadOnlySet<string> nodeIds, IReadOnlySet<string> outputVariables)
    {
        var dot = condition.LastIndexOf('.');
        var suffix = dot > 0 ? condition[(dot + 1)..].ToLowerInvariant() : null;
        if (dot <= 0 || suffix is not ("success" or "failed"))
            return $"{path} must have the form <stepId>.success or <stepId>.failed";

        var stepId = condition[..dot];
        return nodeIds.Contains(stepId) || outputVariables.Contains(stepId)
            ? null
            : $"{path} references unknown step '{stepId}'";
    }

    /// <summary>Validates a structured <c>conditionExpression</c> tree.</summary>
    public static string? ValidateExpression(
        JsonElement expression, string path, IReadOnlySet<string> nodeIds, IReadOnlySet<string> outputVariables)
    {
        var nodeCount = 0;
        return Walk(expression, path, nodeIds, outputVariables, depth: 0, ref nodeCount);
    }

    private static string? Walk(
        JsonElement el, string path, IReadOnlySet<string> nodeIds, IReadOnlySet<string> outputVariables,
        int depth, ref int nodeCount)
    {
        if (++nodeCount > MaxNodes) return $"{path}: condition has too many nodes";
        if (depth > MaxDepth) return $"{path}: condition is nested too deeply";
        if (el.ValueKind != JsonValueKind.Object) return $"{path} must be an object";

        var type = GetString(el, "type");
        switch (type)
        {
            case "group":
            {
                var op = GetString(el, "op")?.ToUpperInvariant();
                if (op is not ("AND" or "OR")) return $"{path}.op must be AND or OR";
                if (!el.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array)
                    return $"{path}.children must be an array";
                var i = 0;
                foreach (var child in children.EnumerateArray())
                {
                    var error = Walk(child, $"{path}.children[{i++}]", nodeIds, outputVariables, depth + 1, ref nodeCount);
                    if (error is not null) return error;
                }
                return null;
            }
            case "not":
                return el.TryGetProperty("child", out var notChild)
                    ? Walk(notChild, $"{path}.child", nodeIds, outputVariables, depth + 1, ref nodeCount)
                    : $"{path}.child is required";
            case "comparison":
                return ValidateComparison(el, path, nodeIds, outputVariables);
            case null:
                return $"{path}.type is required";
            default:
                return $"{path}.type '{type}' is not a condition type";
        }
    }

    private static string? ValidateComparison(
        JsonElement el, string path, IReadOnlySet<string> nodeIds, IReadOnlySet<string> outputVariables)
    {
        var op = GetString(el, "op");
        if (string.IsNullOrEmpty(op)) return $"{path}.op is required";
        var unary = UnaryOps.Contains(op);
        if (!unary && !BinaryOps.Contains(op)) return $"{path}.op '{op}' is not a comparison operator";

        if (!el.TryGetProperty("left", out var left)) return $"{path}.left is required";
        var error = ValidateOperand(left, $"{path}.left", nodeIds, outputVariables);
        if (error is not null) return error;

        if (unary) return null;
        if (!el.TryGetProperty("right", out var right)) return $"{path}.right is required for operator '{op}'";
        return ValidateOperand(right, $"{path}.right", nodeIds, outputVariables);
    }

    private static string? ValidateOperand(
        JsonElement operand, string path, IReadOnlySet<string> nodeIds, IReadOnlySet<string> outputVariables)
    {
        if (operand.ValueKind != JsonValueKind.Object) return $"{path} must be an object";

        var kind = GetString(operand, "kind") ?? "literal";
        if (kind == "literal") return null;
        if (kind != "variable") return $"{path}.kind '{kind}' is not an operand kind";

        var source = (GetString(operand, "source") ?? "step").ToLowerInvariant();
        switch (source)
        {
            case "global":
            case "manual":
                return string.IsNullOrEmpty(GetString(operand, "name")) ? $"{path}.name is required" : null;
            case "step":
                break;
            default:
                // "event" belongs to alerting filters; a workflow run carries no event fields.
                return $"{path}.source '{source}' is not available on a workflow edge";
        }

        var stepId = GetString(operand, "stepId");
        if (string.IsNullOrEmpty(stepId)) return $"{path}.stepId is required";
        if (!nodeIds.Contains(stepId) && !outputVariables.Contains(stepId))
            return $"{path}.stepId references unknown step '{stepId}'";

        var field = GetString(operand, "field") ?? "output";
        if (!StepFields.Contains(field)) return $"{path}.field '{field}' is not an operand field";
        if (field == "param" && string.IsNullOrEmpty(GetString(operand, "paramName")))
            return $"{path}.paramName is required for field 'param'";
        return null;
    }

    private static string? GetString(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}
