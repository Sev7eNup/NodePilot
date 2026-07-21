using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Core.WorkflowDefinitions;

namespace NodePilot.Engine.Execution;

internal static class VariableResolver
{
    // Pre-compiled shared regexes used by both JSON-config expansion and single-string
    // resolution. Also consumed directly by <see cref="PowerShell.PowerShellActivitySupport"/>
    // (GlobalsPattern/StepPattern) for its PowerShell-quoted script-expansion path.
    //
    // StepPattern accepts these property tails:
    //   .output            — stdout (string)
    //   .error             — stderr (string)
    //   .success           — "true"/"false" derived from ActivityResult.Success
    //   .param.<name>      — entry from ActivityResult.OutputParameters
    //
    // .success was added after a test-fixture review (2026-05-17) found placeholders like
    // {{init.success}} that the OLD regex did not recognize — it silently left them as a
    // literal string instead of resolving them or raising the "unresolved template
    // variable" error (the T-7.1 check in StepRunner, which fails a step early if a
    // {{...}} placeholder survives resolution). Including .success in the pattern means
    // it now resolves normally AND is covered by that same fail-fast check when the
    // referenced step is missing or didn't run, matching the other tails' behavior.
    internal static readonly Regex GlobalsPattern = new(@"\{\{globals\.([A-Za-z0-9_\-]+)\}\}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    internal static readonly Regex StepPattern = new(@"\{\{([\w-]+)\.(output|error|success|param\.([\w-]+))\}\}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Output-parameter short-names that will never be aliased into the variables dict
    /// unqualified. Prevents an upstream output from shadowing auth-bearing templates —
    /// a security-audit finding (M-23). Consumers must use the fully-qualified
    /// <c>{{step.param.Authorization}}</c> form instead.
    /// </summary>
    internal static readonly HashSet<string> DenylistedShortParamNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Authorization", "ApiKey", "Api_Key", "Password", "Secret", "Token",
            "Cookie", "Bearer", "X_Api_Key",
        };

    /// <summary>
    /// Assembles the variables dict passed to an activity executor. Sources are layered
    /// so later writes never overwrite earlier ones on a collision:
    ///   1. <c>manual.*</c> — input parameters from manual trigger
    ///   2. <c>globals.*</c> — admin-managed shared constants (read-only)
    ///   3. previous-step OutputParameters: fully-qualified <c>{stepVar}.param.{key}</c>
    ///      always wins; the short-name alias is only added if not already present and
    ///      not in <see cref="DenylistedShortParamNames"/>.
    /// </summary>
    internal static Dictionary<string, string> BuildStepVariables(
        Dictionary<string, string>? inputParameters,
        IReadOnlyDictionary<string, string> globalVariables,
        IReadOnlyDictionary<string, ActivityResult> previousResults,
        List<WorkflowNode> allNodes)
        => BuildStepVariables(inputParameters, globalVariables, previousResults, BuildOutputNameByStepId(allNodes));

    /// <summary>
    /// Hot-path overload: callers who already built an output-name index once per execution
    /// (see <see cref="WorkflowEngine"/>) pass it through directly to skip per-call
    /// node scans.
    /// </summary>
    internal static Dictionary<string, string> BuildStepVariables(
        Dictionary<string, string>? inputParameters,
        IReadOnlyDictionary<string, string> globalVariables,
        IReadOnlyDictionary<string, ActivityResult> previousResults,
        IReadOnlyDictionary<string, string> outputNameByStepId)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (inputParameters is not null)
        {
            foreach (var (key, val) in inputParameters)
                variables[$"manual.{key}"] = val;
        }
        // Globals flow in with a "globals." prefix so templates read
        // {{globals.STRIPE_KEY}} naturally. They go in FIRST so a per-step
        // OutputParameter collision (unlikely — OutputParameters usually have different
        // names) wins. If a workflow deliberately wants to shadow a global with a step
        // output, that's supported; the inverse would be surprising.
        foreach (var (gKey, gVal) in globalVariables)
            variables[$"globals.{gKey}"] = gVal;

        // Inject previous-step outputs + OutputParameters. The flat dict is consumed by
        // PowerShellActivitySupport.ResolveScriptVariables, which looks up `{varName}.output`,
        // `{varName}.error`, and `{varName}.param.{key}` keys verbatim. We mirror the
        // alias-resolution shape used by VariableResolver.BuildVariableMap for the
        // JSON-config path: both the configured outputVariable alias AND the raw stepId
        // resolve to the same value, so `{{step-123.output}}` and `{{myAlias.output}}`
        // are interchangeable in script bodies.
        foreach (var (stepId, prevResult) in previousResults)
        {
            outputNameByStepId.TryGetValue(stepId, out var configuredName);
            var prevVarName = configuredName ?? stepId;

            // .output / .error / .success keys — without these, {{prev.output}} in a
            // runScript body would have stayed literal because the dict only carried
            // .param.* entries. `.success` mirrors the regex's new property tail.
            var successLiteral = prevResult.Success ? "true" : "false";
            variables[$"{prevVarName}.output"] = prevResult.Output ?? string.Empty;
            variables[$"{prevVarName}.error"] = prevResult.ErrorOutput ?? string.Empty;
            variables[$"{prevVarName}.success"] = successLiteral;
            // Raw-stepId fallback so authors can reference the engine-internal id even
            // when an outputVariable alias is set on the producing node.
            if (configuredName is not null && !string.Equals(configuredName, stepId, StringComparison.OrdinalIgnoreCase))
            {
                variables[$"{stepId}.output"] = prevResult.Output ?? string.Empty;
                variables[$"{stepId}.error"] = prevResult.ErrorOutput ?? string.Empty;
                variables[$"{stepId}.success"] = successLiteral;
            }

            foreach (var (paramKey, paramVal) in prevResult.OutputParameters)
            {
                variables[$"{prevVarName}.param.{paramKey}"] = paramVal;
                if (configuredName is not null && !string.Equals(configuredName, stepId, StringComparison.OrdinalIgnoreCase))
                    variables[$"{stepId}.param.{paramKey}"] = paramVal;
                // M-23 (security-audit finding): the short-name alias (`{{paramKey}}` without step prefix) is a
                // convenience for the common single-upstream case but makes the variable
                // namespace attacker-reachable — an upstream activity that emits a parameter
                // called e.g. `Authorization` would shadow a downstream `Authorization`
                // header template.
                // Guardrails:
                //   - collision with an existing short-name → keep the first (trigger-level
                //     input wins), the qualified form still resolves.
                //   - denylist of auth-bearing names → never aliased; templates must use
                //     the fully-qualified `{{step.param.Authorization}}`.
                if (!variables.ContainsKey(paramKey)
                    && !DenylistedShortParamNames.Contains(paramKey))
                {
                    variables[paramKey] = paramVal;
                }
            }
        }

        return variables;
    }


    /// <summary>
    /// JSON string-escape used by template expansion. A security-audit finding (L-12)
    /// noted that this previously only escaped <c>\</c> <c>"</c> <c>\n</c> <c>\r</c>
    /// <c>\t</c> — any other control character (BEL, VT, NUL, …) would land literally in
    /// the JSON body and break <see cref="JsonDocument.Parse"/>, failing the whole step.
    /// We now escape the full 0x00-0x1F range per RFC 8259.
    /// </summary>
    private static string JsonEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                case '\b': sb.Append("\\b");  break;
                case '\f': sb.Append("\\f");  break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Replaces {{varName.output}} and {{varName.error}} placeholders in all string values
    /// within the config JSON element. Uses outputVariable if set, otherwise falls back to stepId.
    /// </summary>
    internal static JsonElement ResolveVariables(JsonElement config, IReadOnlyDictionary<string, ActivityResult> results, List<WorkflowNode> allNodes,
        IReadOnlyDictionary<string, string>? globalVariables = null)
        => ResolveVariables(config, results, BuildOutputVariableAliasMap(allNodes), globalVariables);

    /// <summary>
    /// H-1 (security audit 2026-05-15): same as <see cref="ResolveVariables(JsonElement, IReadOnlyDictionary{string, ActivityResult}, IReadOnlyDictionary{string, string}?, IReadOnlyDictionary{string, string}?)"/>,
    /// but skips substitution for the named top-level properties. Used by SQL/DB-trigger
    /// activities where <c>query</c> text is the wrong place for <c>{{var}}</c> expansion —
    /// it would silently smuggle untrusted values into a raw <c>CommandText</c>. The
    /// engine forces those activities to bind dynamic values through <c>parameters</c> instead.
    /// Only the top-level property names are honoured; nested objects under a non-protected
    /// key are still fully resolved.
    /// </summary>
    internal static JsonElement ResolveVariablesExcept(
        JsonElement config,
        IReadOnlyDictionary<string, ActivityResult> results,
        IReadOnlyDictionary<string, string>? outputVariableToStepId,
        IReadOnlyDictionary<string, string>? globalVariables,
        IReadOnlySet<string> doNotResolveFields)
    {
        if (doNotResolveFields is null || doNotResolveFields.Count == 0
            || config.ValueKind != JsonValueKind.Object)
        {
            return ResolveVariables(config, results, outputVariableToStepId, globalVariables);
        }

        // Re-assemble the object property-by-property: protected fields are passed through
        // verbatim (raw JSON), every other value goes through the standard resolver pass.
        // Per-property StringBuilder is cheaper than JsonNode allocation and keeps numeric
        // / boolean / nested-object literals byte-identical to the input.
        var sb = new StringBuilder();
        sb.Append('{');
        var first = true;
        foreach (var prop in config.EnumerateObject())
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(JsonSerializer.Serialize(prop.Name));
            sb.Append(':');
            if (doNotResolveFields.Contains(prop.Name))
            {
                sb.Append(prop.Value.GetRawText());
            }
            else
            {
                var subResolved = ResolveVariables(prop.Value, results, outputVariableToStepId, globalVariables);
                sb.Append(subResolved.GetRawText());
            }
        }
        sb.Append('}');

        using var doc = JsonDocument.Parse(sb.ToString());
        return doc.RootElement.Clone();
    }

    /// <summary>Hot-path overload taking a pre-built output-variable alias index.</summary>
    internal static JsonElement ResolveVariables(JsonElement config, IReadOnlyDictionary<string, ActivityResult> results,
        IReadOnlyDictionary<string, string>? outputVariableToStepId,
        IReadOnlyDictionary<string, string>? globalVariables = null)
    {
        var configJson = config.GetRawText();
        if (string.IsNullOrEmpty(configJson) || !configJson.Contains("{{"))
            return config;

        var variableMap = BuildVariableMap(results, outputVariableToStepId);

        // First pass: {{globals.NAME}} — admin-managed shared constants. Deliberately comes
        // BEFORE the step-output pass so a global can be referenced in a literal that's later
        // consumed by downstream expansion without a name-collision risk.
        if (globalVariables is not null && globalVariables.Count > 0)
        {
            configJson = GlobalsPattern.Replace(configJson, match =>
            {
                var name = match.Groups[1].Value;
                if (!globalVariables.TryGetValue(name, out var val)) return match.Value;
                // JSON-string-escape: the placeholder lives inside a JSON string literal.
                return JsonEscape(val);
            });
        }

        // Replace {{name.output}}, {{name.error}}, and {{name.param.paramName}} patterns
        // Note: [\w-]+ to support step IDs with hyphens like "step-1776111023799"
        var resolved = StepPattern.Replace(configJson, match =>
        {
            var varName = match.Groups[1].Value;
            var property = match.Groups[2].Value;

            if (!variableMap.TryGetValue(varName, out var result))
                return match.Value; // Leave unresolved

            // Handle {{varName.param.paramName}} — access individual output parameters
            if (property.StartsWith("param.") && match.Groups[3].Success)
            {
                var paramName = match.Groups[3].Value;
                if (result.OutputParameters.TryGetValue(paramName, out var paramValue))
                {
                    return JsonEscape(paramValue);
                }
                return match.Value; // Leave unresolved if param not found
            }

            var value = property.ToLowerInvariant() switch
            {
                "output" => result.Output ?? "",
                "error" => result.ErrorOutput ?? "",
                "success" => result.Success ? "true" : "false",
                _ => match.Value
            };

            // Escape for JSON string context (the placeholder is inside a JSON string value)
            return JsonEscape(value);
        });

        // Dispose the JsonDocument so its pooled buffers return to the ArrayPool.
        // Returning RootElement directly would leak the buffer; Clone() detaches the
        // element from the pooled-document's lifetime so the caller gets a
        // self-contained copy that stays valid after the document is disposed.
        using var doc = JsonDocument.Parse(resolved);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Shared between the JSON-config and the plain-string resolvers. Maps each previous step
    /// by its id AND by its configured <c>outputVariable</c> alias (when set and non-equal).
    /// OrdinalIgnoreCase so <c>{{Step}}</c> and <c>{{STEP}}</c> resolve to the same entry,
    /// matching the PowerShell / template expectation throughout the engine.
    /// </summary>
    private static IReadOnlyDictionary<string, ActivityResult> BuildVariableMap(
        IReadOnlyDictionary<string, ActivityResult> results,
        IReadOnlyDictionary<string, string>? outputVariableToStepId)
    {
        var map = new Dictionary<string, ActivityResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var (stepId, result) in results)
            map[stepId] = result;

        if (outputVariableToStepId is not null)
            foreach (var (alias, stepId) in outputVariableToStepId)
                if (results.TryGetValue(stepId, out var result))
                    map[alias] = result;

        return map;
    }

    /// <summary>
    /// Materialises an id → node lookup for one call-site's batch of resolves. Called from
    /// the legacy <c>List&lt;WorkflowNode&gt;</c> overloads; the hot path in
    /// <see cref="WorkflowEngine"/> constructs the dict once per execution and reuses it.
    /// </summary>
    internal static Dictionary<string, WorkflowNode> BuildNodesById(List<WorkflowNode> allNodes)
        => WorkflowDefinitionDocument.BuildNodesById(allNodes);

    internal static Dictionary<string, string> BuildOutputNameByStepId(IReadOnlyList<WorkflowNode> allNodes)
        => WorkflowDefinitionDocument.BuildOutputNameByStepId(allNodes);

    internal static Dictionary<string, string> BuildOutputVariableAliasMap(IReadOnlyList<WorkflowNode> allNodes)
        => WorkflowDefinitionDocument.BuildOutputVariableAliasMap(allNodes);

    /// <summary>
    /// Resolves {{varName.output}} placeholders in a single string value (not JSON).
    /// Used for targetMachineId and credentialId fields.
    /// </summary>
    internal static string? ResolveStringValue(string? raw, IReadOnlyDictionary<string, ActivityResult> results, List<WorkflowNode> allNodes,
        IReadOnlyDictionary<string, string>? globalVariables = null)
        => ResolveStringValue(raw, results, BuildOutputVariableAliasMap(allNodes), globalVariables);

    /// <summary>Hot-path overload taking a pre-built output-variable alias index.</summary>
    internal static string? ResolveStringValue(string? raw, IReadOnlyDictionary<string, ActivityResult> results,
        IReadOnlyDictionary<string, string>? outputVariableToStepId,
        IReadOnlyDictionary<string, string>? globalVariables = null)
    {
        if (string.IsNullOrWhiteSpace(raw) || !raw.Contains("{{"))
            return raw;

        // First pass: globals. Plain substitution — no JSON escaping because this path
        // feeds simple fields (target machine id, credential id).
        if (globalVariables is not null && globalVariables.Count > 0)
        {
            raw = GlobalsPattern.Replace(raw, m =>
                globalVariables.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
        }

        var variableMap = BuildVariableMap(results, outputVariableToStepId);

        return StepPattern.Replace(raw!, match =>
        {
            var varNameStr = match.Groups[1].Value;
            var propertyStr = match.Groups[2].Value;

            if (variableMap.TryGetValue(varNameStr, out var res) && propertyStr.StartsWith("param.") && match.Groups[3].Success)
            {
                // No Trim — keeps the value byte-identical to ResolveVariables' JSON-config
                // pass. Mismatched trim semantics caused subtle bugs where the same template
                // resolved one way in restApi.url (string-path) and another way in restApi.body
                // (JSON-path).
                return res.OutputParameters.TryGetValue(match.Groups[3].Value, out var pv) ? pv : match.Value;
            }
            var varName = match.Groups[1].Value;
            var property = match.Groups[2].Value;

            if (!variableMap.TryGetValue(varName, out var result))
                return match.Value;

            return property.ToLowerInvariant() switch
            {
                "output" => result.Output ?? "",
                "error" => result.ErrorOutput ?? "",
                "success" => result.Success ? "true" : "false",
                _ => match.Value
            };
        });
    }
}
