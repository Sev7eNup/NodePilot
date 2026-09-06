using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Execution;

namespace NodePilot.Engine.Conditions;

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

/// <summary>
/// Evaluates structured edge condition expressions. Schema:
///   group:      { "type": "group", "op": "AND|OR", "children": [...] }
///   not:        { "type": "not", "child": {...} }
///   comparison: { "type": "comparison", "left": OPERAND, "op": OP, "right": OPERAND? }
/// operand: { "kind": "variable", "stepId": "...", "field": "output|error|success|param", "paramName"?:
/// "..." }
///             | { "kind": "variable", "source": "global|manual|event", "name": "..." }
///             | { "kind": "literal", "value": "..." }
///
/// Supported ops:
///   comparison: == != &lt; &gt; &lt;= &gt;= (numeric if both parseable, else string)
///   string:     contains startsWith endsWith matches (regex)
///   unary:      isEmpty isNotEmpty isTrue isFalse (right omitted)
///
/// Fail-closed policy: a malformed expression (unknown type, operator, source or field, missing
/// operand or child) throws <see cref="ConditionEvaluationException"/>. A variable operand
/// without a value (step without a result, missing param, global or trigger input) makes its
/// comparison undecidable, and an undecidable result never satisfies the condition, not even
/// through <c>not</c>. Event fields come from a declared catalog, so an absent one reads as "".
/// </summary>
public static class ConditionEvaluator
{
    /// <summary>Three-valued result: a comparison on a missing value is neither true nor false.</summary>
    private enum Outcome { False, True, Unknown }

    private static readonly HashSet<string> UnaryOps = ["isEmpty", "isNotEmpty", "isTrue", "isFalse"];
    private static readonly HashSet<string> BinaryOps =
        ["==", "!=", "<", ">", "<=", ">=", "contains", "startsWith", "endsWith", "matches"];

    public static bool Evaluate(JsonElement expression, IReadOnlyDictionary<string, ActivityResult> results,
        IReadOnlyDictionary<string, string>? outputVariableToStepId = null,
        IReadOnlyDictionary<string, string>? globalVariables = null,
        IReadOnlyDictionary<string, string>? inputParameters = null)
        => Evaluate(expression, new ConditionContext(results, outputVariableToStepId, globalVariables, inputParameters));

    /// <summary>True only when the expression is decidable and holds.</summary>
    /// <exception cref="ConditionEvaluationException">The expression is malformed.</exception>
    public static bool Evaluate(JsonElement expression, ConditionContext ctx)
        => EvaluateOutcome(expression, ctx) == Outcome.True;

    private static Outcome EvaluateOutcome(JsonElement expression, ConditionContext ctx)
    {
        if (expression.ValueKind != JsonValueKind.Object)
            throw new ConditionEvaluationException("condition must be an object");

        var type = GetString(expression, "type");
        return type switch
        {
            "group" => EvaluateGroup(expression, ctx),
            "not" => expression.TryGetProperty("child", out var child)
                ? Negate(EvaluateOutcome(child, ctx))
                : throw new ConditionEvaluationException("'not' requires a child condition"),
            "comparison" => EvaluateComparison(expression, ctx),
            null => throw new ConditionEvaluationException("condition has no 'type'"),
            _ => throw new ConditionEvaluationException($"unknown condition type '{type}'"),
        };
    }

    private static Outcome Negate(Outcome outcome) => outcome switch
    {
        Outcome.True => Outcome.False,
        Outcome.False => Outcome.True,
        _ => Outcome.Unknown,
    };

    private static Outcome EvaluateGroup(JsonElement group, ConditionContext ctx)
    {
        var op = GetString(group, "op")?.ToUpperInvariant();
        if (op is not ("AND" or "OR"))
            throw new ConditionEvaluationException("group 'op' must be AND or OR");
        if (!group.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array)
            throw new ConditionEvaluationException("group requires a 'children' array");

        var undecided = false;
        if (op == "OR")
        {
            foreach (var child in children.EnumerateArray())
            {
                var outcome = EvaluateOutcome(child, ctx);
                if (outcome == Outcome.True) return Outcome.True;
                if (outcome == Outcome.Unknown) undecided = true;
            }
            return undecided ? Outcome.Unknown : Outcome.False;
        }

        foreach (var child in children.EnumerateArray())
        {
            var outcome = EvaluateOutcome(child, ctx);
            if (outcome == Outcome.False) return Outcome.False;
            if (outcome == Outcome.Unknown) undecided = true;
        }
        return undecided ? Outcome.Unknown : Outcome.True;
    }

    private static Outcome EvaluateComparison(JsonElement cmp, ConditionContext ctx)
    {
        var op = GetString(cmp, "op")
                 ?? throw new ConditionEvaluationException("comparison has no 'op'");
        var unary = UnaryOps.Contains(op);
        if (!unary && !BinaryOps.Contains(op))
            throw new ConditionEvaluationException($"unknown comparison operator '{op}'");
        if (!cmp.TryGetProperty("left", out var l))
            throw new ConditionEvaluationException("comparison has no left operand");

        var (left, leftResolved) = ResolveOperand(l, ctx);

        if (unary)
        {
            if (!leftResolved) return Outcome.Unknown;
            return ToOutcome(op switch
            {
                "isEmpty" => string.IsNullOrEmpty(left),
                "isNotEmpty" => !string.IsNullOrEmpty(left),
                "isTrue" => IsTruthy(left),
                _ => !IsTruthy(left),
            });
        }

        if (!cmp.TryGetProperty("right", out var r))
            throw new ConditionEvaluationException($"operator '{op}' requires a right operand");
        var (right, rightResolved) = ResolveOperand(r, ctx);
        if (!leftResolved || !rightResolved) return Outcome.Unknown;

        return ToOutcome(op switch
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
            _ => TryRegexMatch(left, right),
        });
    }

    private static Outcome ToOutcome(bool value) => value ? Outcome.True : Outcome.False;

    private static string? GetString(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    // Step-output and globals templates reuse VariableResolver's compiled patterns — this
    // evaluator already depends on Engine.Execution (see EvaluateEdge below), so keeping a
    // second copy of the same two patterns only risked them drifting apart. `manual.` has no
    // counterpart there and runs as its own pre-pass; like globals it carries no step-shaped tail.
    private static readonly Regex ManualTemplateRegex = new(@"\{\{manual\.([A-Za-z0-9_\-]+)\}\}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Resolves an operand to its string value. <c>Resolved</c> is false when a variable operand
    /// has no value in this run; literals always resolve.
    /// </summary>
    private static (string Value, bool Resolved) ResolveOperand(JsonElement operand, ConditionContext ctx)
    {
        if (operand.ValueKind != JsonValueKind.Object)
            throw new ConditionEvaluationException("operand must be an object");
        var kind = GetString(operand, "kind") ?? "literal";

        if (kind == "literal")
        {
            var raw = operand.TryGetProperty("value", out var v)
                ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText())
                : "";
            return (ResolveTemplates(raw, ctx), true);
        }

        if (kind != "variable")
            throw new ConditionEvaluationException($"unknown operand kind '{kind}'");

        // Source discriminator, defaults to "step" for operands that omit it. When source
        // is "global" / "manual" / "event", the operand carries a flat `name` instead of stepId/field.
        var source = GetString(operand, "source") ?? "step";
        if (string.Equals(source, "global", StringComparison.OrdinalIgnoreCase))
        {
            var name = RequireName(operand, source);
            return ctx.GlobalVariables is not null && ctx.GlobalVariables.TryGetValue(name, out var gv)
                ? (gv, true)
                : ("", false);
        }
        if (string.Equals(source, "manual", StringComparison.OrdinalIgnoreCase))
        {
            var name = RequireName(operand, source);
            return ctx.InputParameters is not null && ctx.InputParameters.TryGetValue(name, out var mv)
                ? (mv, true)
                : ("", false);
        }
        if (string.Equals(source, "event", StringComparison.OrdinalIgnoreCase))
        {
            // Alerting rule filters: the operand carries a flat event-field `name` resolved
            // against the NotificationContext field map. The map is a declared catalog, so a
            // field that is not present on this event reads as empty rather than undecidable.
            var name = RequireName(operand, source);
            if (ctx.EventFields is null) return ("", false);
            return (ctx.EventFields.TryGetValue(name, out var ev) ? ev : "", true);
        }
        if (!string.Equals(source, "step", StringComparison.OrdinalIgnoreCase))
            throw new ConditionEvaluationException($"unknown operand source '{source}'");

        var stepId = GetString(operand, "stepId");
        if (string.IsNullOrEmpty(stepId))
            throw new ConditionEvaluationException("step operand requires a 'stepId'");

        // Resolve via outputVariable alias if stepId is actually a variable name
        if (!ctx.Results.ContainsKey(stepId) && ctx.OutputVariableToStepId is not null
            && ctx.OutputVariableToStepId.TryGetValue(stepId, out var mapped))
            stepId = mapped;

        var field = GetString(operand, "field") ?? "output";
        string? paramName = null;
        if (field == "param")
        {
            paramName = GetString(operand, "paramName");
            if (string.IsNullOrEmpty(paramName))
                throw new ConditionEvaluationException("operand field 'param' requires a 'paramName'");
        }
        else if (field is not ("output" or "error" or "success"))
        {
            throw new ConditionEvaluationException($"unknown operand field '{field}'");
        }

        if (!ctx.Results.TryGetValue(stepId, out var result)) return ("", false);

        return field switch
        {
            "output" => (result.Output ?? "", true),
            "error" => (result.ErrorOutput ?? "", true),
            "success" => (result.Success ? "true" : "false", true),
            _ => result.OutputParameters.TryGetValue(paramName!, out var pv) ? (pv, true) : ("", false),
        };
    }

    private static string RequireName(JsonElement operand, string source)
    {
        var name = GetString(operand, "name");
        if (string.IsNullOrEmpty(name))
            throw new ConditionEvaluationException($"{source} operand requires a 'name'");
        return name;
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

        // An empty operand sorts before every digit, so the ordinal fallback would make
        // "value < threshold" true exactly when the value is empty. Ordering needs two values.
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
    /// Legacy string-shaped condition: "stepId.success" or "stepId.failed", where the step part
    /// is a node id or an output-variable alias. Any other shape throws; a referenced step
    /// without a result in this run is false.
    /// </summary>
    internal static bool EvaluateLegacy(string condition, IReadOnlyDictionary<string, ActivityResult> results,
        IReadOnlyDictionary<string, string>? outputVariableToStepId = null)
    {
        var dot = condition.LastIndexOf('.');
        var suffix = dot > 0 ? condition[(dot + 1)..].ToLowerInvariant() : null;
        if (dot <= 0 || suffix is not ("success" or "failed"))
        {
            throw new ConditionEvaluationException(
                $"condition '{condition}' must have the form <stepId>.success or <stepId>.failed");
        }

        var stepId = condition[..dot];
        if (!results.ContainsKey(stepId) && outputVariableToStepId is not null
            && outputVariableToStepId.TryGetValue(stepId, out var mapped))
            stepId = mapped;

        if (!results.TryGetValue(stepId, out var result)) return false;
        return suffix == "success" ? result.Success : !result.Success;
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
            return EvaluateLegacy(edge.Condition, results, outputVariableToStepId);

        return true;
    }
}
