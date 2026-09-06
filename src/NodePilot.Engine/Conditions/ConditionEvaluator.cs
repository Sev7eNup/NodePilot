using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Execution;

namespace NodePilot.Engine.Conditions;

/// <summary>
/// Evaluates structured edge condition expressions. Schema:
///   group:      { "type": "group", "op": "AND|OR", "children": [...] }
///   not:        { "type": "not", "child": {...} }
///   comparison: { "type": "comparison", "left": OPERAND, "op": OP, "right": OPERAND? }
/// operand: { "kind": "variable", "stepId": "...", "field": "output|error|param", "paramName"?:
/// "..." }
///             | { "kind": "literal", "value": "..." }
///
/// Supported ops:
///   comparison: == != &lt; &gt; &lt;= &gt;= (numeric if both parseable, else string)
///   string:     contains startsWith endsWith matches (regex)
///   unary:      isEmpty isNotEmpty isTrue isFalse (right omitted)
///
/// Safe-fail policy: unresolved variables evaluate to empty string; all comparisons
/// against them return false (except != and isEmpty/isNotEmpty).
/// </summary>
/// <summary>
/// Run-time inputs available to an edge condition. Bundles the three substitution sources
/// — previous step results, workflow globals, and manual-trigger parameters — into a single
/// parameter so call sites don't grow extra optional args every time a new source lands.
/// </summary>
public readonly record struct ConditionContext(
    IReadOnlyDictionary<string, ActivityResult> Results,
    IReadOnlyDictionary<string, string>? OutputVariableToStepId,
    IReadOnlyDictionary<string, string>? GlobalVariables,
    IReadOnlyDictionary<string, string>? InputParameters,
    // Flat event-field map for alerting rule filters (operands of source "event"). Null for edge
    // conditions, which never use that source — so this is fully backward-compatible.
    IReadOnlyDictionary<string, string>? EventFields = null);

public static class ConditionEvaluator
{
    public static bool Evaluate(JsonElement expression, IReadOnlyDictionary<string, ActivityResult> results,
        IReadOnlyDictionary<string, string>? outputVariableToStepId = null,
        IReadOnlyDictionary<string, string>? globalVariables = null,
        IReadOnlyDictionary<string, string>? inputParameters = null)
        => Evaluate(expression, new ConditionContext(results, outputVariableToStepId, globalVariables, inputParameters));

    public static bool Evaluate(JsonElement expression, ConditionContext ctx)
    {
        if (expression.ValueKind != JsonValueKind.Object) return true;
        var type = expression.TryGetProperty("type", out var t) ? t.GetString() : null;

        return type switch
        {
            "group" => EvaluateGroup(expression, ctx),
            "not" => !(expression.TryGetProperty("child", out var c)
                        && Evaluate(c, ctx)),
            "comparison" => EvaluateComparison(expression, ctx),
            _ => true,
        };
    }

    private static bool EvaluateGroup(JsonElement group, ConditionContext ctx)
    {
        var op = (group.TryGetProperty("op", out var o) ? o.GetString() : "AND")?.ToUpperInvariant() ?? "AND";
        if (!group.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array)
            return true;

        if (op == "OR")
        {
            foreach (var child in children.EnumerateArray())
                if (Evaluate(child, ctx)) return true;
            return false;
        }
        // default AND
        foreach (var child in children.EnumerateArray())
            if (!Evaluate(child, ctx)) return false;
        return true;
    }

    private static bool EvaluateComparison(JsonElement cmp, ConditionContext ctx)
    {
        var op = (cmp.TryGetProperty("op", out var o) ? o.GetString() : null) ?? "==";
        var left = cmp.TryGetProperty("left", out var l) ? ResolveOperand(l, ctx) : "";

        // Unary operators
        switch (op)
        {
            case "isEmpty": return string.IsNullOrEmpty(left);
            case "isNotEmpty": return !string.IsNullOrEmpty(left);
            case "isTrue": return IsTruthy(left);
            // An unresolved operand is "", which is not truthy — without the length check
            // `isFalse` would be true for a value that never arrived, the same fail-open
            // inversion CompareNumeric guards against.
            case "isFalse": return left.Length > 0 && !IsTruthy(left);
        }

        var right = cmp.TryGetProperty("right", out var r) ? ResolveOperand(r, ctx) : "";

        return op switch
        {
            "==" => CompareEquals(left, right),
            "!=" => !CompareEquals(left, right),
            "<" => CompareNumeric(left, right, (a, b) => a < b),
            ">" => CompareNumeric(left, right, (a, b) => a > b),
            "<=" => CompareNumeric(left, right, (a, b) => a <= b),
            ">=" => CompareNumeric(left, right, (a, b) => a >= b),
            "contains" => left.Contains(right, StringComparison.Ordinal),
            "startsWith" => left.StartsWith(right, StringComparison.Ordinal),
            "endsWith" => left.EndsWith(right, StringComparison.Ordinal),
            "matches" => TryRegexMatch(left, right),
            _ => false,
        };
    }

    // Step-output and globals templates reuse VariableResolver's compiled patterns — this
    // evaluator already depends on Engine.Execution (see EvaluateEdge below), so keeping a
    // second copy of the same two patterns only risked them drifting apart. `manual.` has no
    // counterpart there and runs as its own pre-pass; like globals it carries no step-shaped tail.
    private static readonly Regex ManualTemplateRegex = new(@"\{\{manual\.([A-Za-z0-9_\-]+)\}\}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static string ResolveOperand(JsonElement operand, ConditionContext ctx)
    {
        if (operand.ValueKind != JsonValueKind.Object) return "";
        var kind = operand.TryGetProperty("kind", out var k) ? k.GetString() : "literal";

        if (kind == "literal")
        {
            var raw = operand.TryGetProperty("value", out var v)
                ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText())
                : "";
            return ResolveTemplates(raw, ctx);
        }

        if (kind != "variable") return "";

        // Source discriminator, defaults to "step" for operands that omit it. When source
        // is "global" / "manual", the operand carries a flat `name` instead of stepId/field.
        var source = operand.TryGetProperty("source", out var srcEl) ? srcEl.GetString() : "step";
        if (string.Equals(source, "global", StringComparison.OrdinalIgnoreCase))
        {
            var name = operand.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
            if (string.IsNullOrEmpty(name) || ctx.GlobalVariables is null) return "";
            return ctx.GlobalVariables.TryGetValue(name, out var gv) ? gv : "";
        }
        if (string.Equals(source, "manual", StringComparison.OrdinalIgnoreCase))
        {
            var name = operand.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
            if (string.IsNullOrEmpty(name) || ctx.InputParameters is null) return "";
            return ctx.InputParameters.TryGetValue(name, out var mv) ? mv : "";
        }
        if (string.Equals(source, "event", StringComparison.OrdinalIgnoreCase))
        {
            // Alerting rule filters: the operand carries a flat event-field `name` resolved
            // against the NotificationContext field map.
            var name = operand.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
            if (string.IsNullOrEmpty(name) || ctx.EventFields is null) return "";
            return ctx.EventFields.TryGetValue(name, out var ev) ? ev : "";
        }

        var stepId = operand.TryGetProperty("stepId", out var sid) ? sid.GetString() : null;
        if (string.IsNullOrEmpty(stepId)) return "";

        // Resolve via outputVariable alias if stepId is actually a variable name
        if (!ctx.Results.ContainsKey(stepId) && ctx.OutputVariableToStepId is not null
            && ctx.OutputVariableToStepId.TryGetValue(stepId, out var mapped))
            stepId = mapped;

        if (!ctx.Results.TryGetValue(stepId, out var result)) return "";

        var field = operand.TryGetProperty("field", out var f) ? f.GetString() : "output";
        return field switch
        {
            "output" => result.Output ?? "",
            "error" => result.ErrorOutput ?? "",
            "success" => result.Success ? "true" : "false",
            "param" => operand.TryGetProperty("paramName", out var pn) && pn.GetString() is { } name
                        && result.OutputParameters.TryGetValue(name, out var pv) ? pv : "",
            _ => "",
        };
    }

    /// <summary>
    /// Number styles for operand parsing. Deliberately excludes <c>AllowThousands</c>, which
    /// <c>NumberStyles.Any</c> includes: the invariant group separator is "," and .NET does not
    /// validate group placement, so a locale-formatted "1,5" parses as 15 instead of failing.
    /// Without the flag such a value falls through to the string path, which is wrong but visible,
    /// rather than comparing as a number two orders of magnitude off.
    /// </summary>
    private const NumberStyles OperandNumberStyles = NumberStyles.Float;

    private static bool CompareEquals(string a, string b)
    {
        if (decimal.TryParse(a, OperandNumberStyles, CultureInfo.InvariantCulture, out var da)
            && decimal.TryParse(b, OperandNumberStyles, CultureInfo.InvariantCulture, out var db))
            return da == db;
        return string.Equals(a, b, StringComparison.Ordinal);
    }

    private static bool CompareNumeric(string a, string b, Func<decimal, decimal, bool> cmp)
    {
        if (decimal.TryParse(a, OperandNumberStyles, CultureInfo.InvariantCulture, out var da)
            && decimal.TryParse(b, OperandNumberStyles, CultureInfo.InvariantCulture, out var db))
            return cmp(da, db);

        // Safe-fail on a missing operand. An unresolved variable resolves to "", which sorts
        // before every digit, so the ordinal fallback below would make "value < threshold" true
        // exactly when the value is absent — firing the guarded branch on missing data. The
        // policy documented on this class is that comparisons against an unresolved operand are
        // false.
        if (a.Length == 0 || b.Length == 0) return false;

        // String ordering fallback
        var c = string.Compare(a, b, StringComparison.Ordinal);
        return cmp(c, 0);
    }

    // Guards against catastrophic-backtracking regexes authored in edge conditions. The
    // timeout is short because an edge condition sits on the critical path between every
    // pair of steps. Pattern length is capped at 2 KiB, enough for hand-written patterns.
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(200);
    private const int RegexMaxInputLength = 1024 * 1024;
    private const int RegexMaxPatternLength = 2048;

    // Compiled-pattern cache: edge conditions reuse the same `matches` pattern across every
    // run of a workflow, so this avoids recompiling the regex on each evaluation. Bounded at
    // 256 entries to limit growth from caller-controlled input; past the cap, a compiled
    // instance is still returned but not cached.
    private static readonly ConcurrentDictionary<string, Regex?> RegexCache = new();
    private const int RegexCacheMaxSize = 256;

    private static Regex? GetCachedRegex(string pattern)
    {
        if (RegexCache.TryGetValue(pattern, out var cached)) return cached;

        Regex? compiled;
        try
        {
            // Case-insensitivity is expressible inline via `(?i)` — keeps the option surface
            // narrow and stops a debugger-friendly Regex flag from changing interpretation.
            compiled = new Regex(pattern, RegexOptions.None, RegexMatchTimeout);
        }
        catch (ArgumentException)
        {
            // Cache the failure as null so we don't re-throw on every evaluation of a
            // permanently-invalid pattern.
            compiled = null;
        }

        if (RegexCache.Count < RegexCacheMaxSize)
            RegexCache.TryAdd(pattern, compiled);

        return compiled;
    }

    private static bool TryRegexMatch(string value, string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern.Length > RegexMaxPatternLength) return false;
        var rx = GetCachedRegex(pattern);
        if (rx is null) return false;
        try
        {
            var input = value.Length > RegexMaxInputLength ? value[..RegexMaxInputLength] : value;
            return rx.IsMatch(input);
        }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static string ResolveTemplates(string raw, ConditionContext ctx)
    {
        if (string.IsNullOrEmpty(raw) || !raw.Contains("{{")) return raw;

        // First pass: {{globals.X}} — admin-managed shared constants. Runs BEFORE the
        // step-pattern pass so a global referenced from a literal operand can't be
        // mis-classified as an unresolved step (and silently return "").
        if (ctx.GlobalVariables is not null && ctx.GlobalVariables.Count > 0)
        {
            raw = VariableResolver.GlobalsPattern.Replace(raw, m =>
                ctx.GlobalVariables.TryGetValue(m.Groups[1].Value, out var gv) ? gv : m.Value);
        }

        // Second pass: {{manual.X}} — input parameters from the triggering call (manualTrigger
        // params, webhook payload keys, external-trigger inputs).
        if (ctx.InputParameters is not null && ctx.InputParameters.Count > 0)
        {
            raw = ManualTemplateRegex.Replace(raw, m =>
                ctx.InputParameters.TryGetValue(m.Groups[1].Value, out var mv) ? mv : m.Value);
        }

        // The substitution body stays local: it resolves against ctx.Results with that dict's
        // own comparer (ordinal for the engine's result map), whereas VariableResolver merges
        // results + aliases into an OrdinalIgnoreCase map. Routing through it would change how
        // differently-cased step ids resolve in conditions.
        return VariableResolver.StepPattern.Replace(raw, m =>
        {
            var name = m.Groups[1].Value;
            if (!ctx.Results.ContainsKey(name) && ctx.OutputVariableToStepId is not null
                && ctx.OutputVariableToStepId.TryGetValue(name, out var mapped))
                name = mapped;
            if (!ctx.Results.TryGetValue(name, out var result)) return m.Value;
            var prop = m.Groups[2].Value;
            if (prop.StartsWith("param.") && m.Groups[3].Success)
                return result.OutputParameters.TryGetValue(m.Groups[3].Value, out var p) ? p : m.Value;
            return prop switch
            {
                "output" => result.Output ?? "",
                "error" => result.ErrorOutput ?? "",
                "success" => result.Success ? "true" : "false",
                _ => m.Value,
            };
        });
    }

    private static bool IsTruthy(string v)
        => !string.IsNullOrEmpty(v)
           && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(v, "0", StringComparison.Ordinal);

    /// <summary>
    /// Legacy string-shaped condition: "stepId.success" or "stepId.failed". Any other
    /// shape or unknown step falls through to true so non-gated edges keep working.
    /// </summary>
    internal static bool EvaluateLegacy(string condition, IReadOnlyDictionary<string, ActivityResult> results)
    {
        var parts = condition.Split('.');
        if (parts.Length == 2 && results.TryGetValue(parts[0], out var result))
        {
            return parts[1].ToLowerInvariant() switch
            {
                "success" => result.Success,
                "failed" => !result.Success,
                _ => true
            };
        }
        return true;
    }

    /// <summary>
    /// Evaluates an edge's routing condition. Priority order:
    ///   1. <c>conditionExpression</c> (structured AST) if present
    ///   2. <c>condition</c> (legacy string) if present
    ///   3. default true
    /// </summary>
    internal static bool EvaluateEdge(WorkflowEdge edge, IReadOnlyDictionary<string, ActivityResult> results,
        List<WorkflowNode> allNodes)
        => EvaluateEdge(edge, results, VariableResolver.BuildOutputVariableAliasMap(allNodes), null, null);

    internal static bool EvaluateEdge(WorkflowEdge edge, IReadOnlyDictionary<string, ActivityResult> results,
        IReadOnlyDictionary<string, string>? outputVariableToStepId,
        IReadOnlyDictionary<string, string>? globalVariables = null,
        IReadOnlyDictionary<string, string>? inputParameters = null)
    {
        if (edge.ConditionExpression is { } expr)
            return Evaluate(expr, new ConditionContext(results, outputVariableToStepId, globalVariables, inputParameters));

        if (!string.IsNullOrEmpty(edge.Condition))
            return EvaluateLegacy(edge.Condition, results);

        return true;
    }
}
