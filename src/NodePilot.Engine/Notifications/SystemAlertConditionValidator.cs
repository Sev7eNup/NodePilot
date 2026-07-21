using System.Globalization;
using System.Text.Json;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;

namespace NodePilot.Engine.Notifications;

/// <summary>A single condition-validation problem, with a JSON-pointer-ish path into the AST.</summary>
public sealed record SystemAlertValidationError(string Path, string Message);

/// <summary>
/// Strict validator for a System-alert policy's condition AST (see ADR 0008, the design
/// decision that introduced system-alert policies as a single alerting pipeline). Unlike the permissive
/// workflow-edge <c>ConditionEvaluator</c> (which fails open — unknown nodes evaluate to true), this rejects
/// a malformed or nonsensical policy filter at save time with field-level errors: it checks the AST shape,
/// that every referenced field is one the source declares, operator/type compatibility, the presence/absence
/// of a right operand per operator arity, numeric-literal parseability, and depth/node/regex caps. It does
/// NOT change the evaluator's runtime semantics — it only gates what a policy may persist.
/// </summary>
public static class SystemAlertConditionValidator
{
    private const int MaxDepth = 20;
    private const int MaxNodes = 200;
    private const int MaxRegexLength = 2048;

    private static readonly HashSet<string> UnaryOps = ["isEmpty", "isNotEmpty", "isTrue", "isFalse"];
    private static readonly HashSet<string> NumericOps = ["==", "!=", "<", ">", "<=", ">="];

    /// <summary>
    /// Validates <paramref name="conditionJson"/> against a source's declared <paramref name="fields"/>. An
    /// empty/null condition is valid (matches everything). Returns true with no errors when the AST is sound.
    /// </summary>
    public static bool TryValidate(string? conditionJson, IReadOnlyList<SystemAlertField> fields, out IReadOnlyList<SystemAlertValidationError> errors)
    {
        var errs = new List<SystemAlertValidationError>();
        errors = errs;
        if (string.IsNullOrWhiteSpace(conditionJson)) return true;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(conditionJson); }
        catch (JsonException ex) { errs.Add(new("$", $"condition is not valid JSON: {ex.Message}")); return false; }

        using (doc)
        {
            var byName = new Dictionary<string, SystemAlertField>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in fields) byName[f.Name] = f;
            var nodeCount = 0;
            Walk(doc.RootElement, "$", byName, errs, depth: 0, ref nodeCount);
        }
        return errs.Count == 0;
    }

    private static void Walk(JsonElement el, string path, IReadOnlyDictionary<string, SystemAlertField> byName,
        List<SystemAlertValidationError> errs, int depth, ref int nodeCount)
    {
        if (++nodeCount > MaxNodes) { errs.Add(new(path, "condition has too many nodes")); return; }
        if (depth > MaxDepth) { errs.Add(new(path, "condition is nested too deeply")); return; }
        if (el.ValueKind != JsonValueKind.Object) { errs.Add(new(path, "expected an object node")); return; }

        var type = el.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "group":
                var op = el.TryGetProperty("op", out var o) ? o.GetString() : null;
                if (!string.Equals(op, "AND", StringComparison.OrdinalIgnoreCase) && !string.Equals(op, "OR", StringComparison.OrdinalIgnoreCase))
                    errs.Add(new($"{path}.op", "group op must be AND or OR"));
                if (!el.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array)
                    errs.Add(new($"{path}.children", "group requires a children array"));
                else
                {
                    var i = 0;
                    foreach (var child in children.EnumerateArray())
                        Walk(child, $"{path}.children[{i++}]", byName, errs, depth + 1, ref nodeCount);
                }
                break;
            case "not":
                if (!el.TryGetProperty("child", out var notChild))
                    errs.Add(new($"{path}.child", "not requires a child"));
                else
                    Walk(notChild, $"{path}.child", byName, errs, depth + 1, ref nodeCount);
                break;
            case "comparison":
                ValidateComparison(el, path, byName, errs);
                break;
            default:
                errs.Add(new($"{path}.type", $"unknown node type '{type}'"));
                break;
        }
    }

    private static void ValidateComparison(JsonElement el, string path,
        IReadOnlyDictionary<string, SystemAlertField> byName, List<SystemAlertValidationError> errs)
    {
        var op = el.TryGetProperty("op", out var o) ? o.GetString() : null;
        if (string.IsNullOrEmpty(op)) { errs.Add(new($"{path}.op", "comparison requires an op")); return; }

        SystemAlertField? field = null;
        if (!el.TryGetProperty("left", out var left))
            errs.Add(new($"{path}.left", "comparison requires a left operand"));
        else
            field = ValidateFieldOperand(left, $"{path}.left", byName, errs);

        if (field is not null && !field.Operators.Contains(op))
            errs.Add(new($"{path}.op", $"operator '{op}' is not valid for field '{field.Name}' ({field.Type})"));

        var unary = UnaryOps.Contains(op);
        var hasRight = el.TryGetProperty("right", out var right);
        if (unary && hasRight) errs.Add(new($"{path}.right", $"operator '{op}' takes no right operand"));
        if (!unary && !hasRight) errs.Add(new($"{path}.right", $"operator '{op}' requires a right operand"));

        if (!unary && hasRight)
        {
            var literal = RightLiteral(right);
            if (op == "matches" && (literal?.Length ?? 0) > MaxRegexLength)
                errs.Add(new($"{path}.right", $"regex pattern must be {MaxRegexLength} characters or less"));

            if (field is { Type: SystemAlertFieldType.Number or SystemAlertFieldType.Duration }
                && NumericOps.Contains(op) && op is not ("==" or "!=")
                && literal is not null
                && !decimal.TryParse(literal, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                errs.Add(new($"{path}.right", $"'{literal}' is not a number for numeric field '{field.Name}'"));
        }
    }

    private static SystemAlertField? ValidateFieldOperand(JsonElement operand, string path,
        IReadOnlyDictionary<string, SystemAlertField> byName, List<SystemAlertValidationError> errs)
    {
        if (operand.ValueKind != JsonValueKind.Object) { errs.Add(new(path, "operand must be an object")); return null; }
        var kind = operand.TryGetProperty("kind", out var k) ? k.GetString() : null;
        if (kind != "variable") { errs.Add(new($"{path}.kind", "left operand must be a field variable")); return null; }
        var source = operand.TryGetProperty("source", out var s) ? s.GetString() : null;
        if (!string.Equals(source, "event", StringComparison.OrdinalIgnoreCase))
        { errs.Add(new($"{path}.source", "operand source must be 'event'")); return null; }
        var name = operand.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrEmpty(name)) { errs.Add(new($"{path}.name", "field name is required")); return null; }
        if (!byName.TryGetValue(name, out var field)) { errs.Add(new($"{path}.name", $"unknown field '{name}'")); return null; }
        return field;
    }

    private static string? RightLiteral(JsonElement right)
    {
        if (right.ValueKind != JsonValueKind.Object) return null;
        if (!right.TryGetProperty("value", out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
    }
}
