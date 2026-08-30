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
    // .success must stay listed here: a tail this pattern does not recognize is left as a
    // literal placeholder instead of being resolved or flagged by the unresolved-template
    // check in StepRunner (T-7.1).
    internal static readonly Regex GlobalsPattern = new(@"\{\{globals\.([A-Za-z0-9_\-]+)\}\}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // {{manual.NAME}} — the run's trigger inputs. Every trigger seeds its event data into the
    // run under this namespace (manualTrigger's declared parameters, the webhook body, the
    // watched file path, ...), and it is the form the README, the designer's variable picker,
    // the ForEach hint and the AI prompt catalog all tell authors to write.
    //
    // It needs its own pattern because the tail after the dot is a user-chosen name, not one
    // of StepPattern's four fixed property tails, so StepPattern cannot match it. Without a
    // dedicated pattern the placeholder would stay unresolved and would also skip the
    // unresolved-template check, since that check only scans step patterns.
    internal static readonly Regex ManualPattern = new(@"\{\{manual\.([A-Za-z0-9_\-]+)\}\}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    internal static readonly Regex StepPattern = new(@"\{\{([\w-]+)\.(output|error|success|param\.([\w-]+))\}\}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Output-parameter short-names that are never aliased into the variables dict
    /// unqualified. Prevents an upstream output from shadowing auth-bearing templates.
    /// Consumers must use the fully-qualified <c>{{step.param.Authorization}}</c> form instead.
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
    ///      always wins; the unqualified short-name alias is added only when exactly one
    ///      ancestor publishes that name, it is not already present, and it is not in
    ///      <see cref="DenylistedShortParamNames"/>.
    ///
    /// Callers pass the output-name index they already built once per execution
    /// (see <see cref="WorkflowEngine"/>) so nothing rescans the node list per call.
    /// </summary>
    internal static Dictionary<string, string> BuildStepVariables(
        Dictionary<string, string>? inputParameters,
        IReadOnlyDictionary<string, string> globalVariables,
        IReadOnlyDictionary<string, ActivityResult> previousResults,
        IReadOnlyDictionary<string, string> outputNameByStepId)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var shortNameCandidates = new Dictionary<string, ShortNameCandidate>(StringComparer.OrdinalIgnoreCase);
        if (inputParameters is not null)
        {
            foreach (var (key, val) in inputParameters)
                variables[$"manual.{key}"] = val;
        }
        // Globals flow in with a "globals." prefix so templates read {{globals.STRIPE_KEY}}
        // naturally. They are added first so a step OutputParameter with the same name wins,
        // letting a workflow intentionally shadow a global with a step output.
        foreach (var (gKey, gVal) in globalVariables)
            variables[$"globals.{gKey}"] = gVal;

        // Inject previous-step outputs and OutputParameters into the flat dict consumed by
        // PowerShellActivitySupport.ResolveScriptVariables
        // (`{varName}.output/.error/.param.{key}`).
        // Both the configured outputVariable alias and the raw stepId map to the same value,
        // so `{{step-123.output}}` and `{{myAlias.output}}` are interchangeable in scripts.
        foreach (var (stepId, prevResult) in previousResults)
        {
            outputNameByStepId.TryGetValue(stepId, out var configuredName);
            var prevVarName = configuredName ?? stepId;

            // .output / .error / .success keys. Without these, {{prev.output}} in a
            // runScript body would stay literal because the dict would only carry
            // .param.* entries.
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

                // Count producers per name; the short-name alias is decided afterwards, once
                // every ancestor has been seen.
                if (shortNameCandidates.TryGetValue(paramKey, out var seen))
                    shortNameCandidates[paramKey] = seen with { ProducerCount = seen.ProducerCount + 1 };
                else
                    shortNameCandidates[paramKey] = new ShortNameCandidate(paramVal, 1);
            }
        }

        // Short-name alias (`hostName`, no step prefix): a convenience for the common
        // single-producer case. It is only created when exactly ONE ancestor publishes that
        // name. Two producers used to race for it — the winner came from HashSet enumeration
        // order over the ancestor set, which is hash-based and, because .NET randomises string
        // hashing per process, could change from one API restart to the next. A published value
        // has one owner, so an ambiguous name gets no owner-less alias; the qualified
        // `{{step.param.hostName}}` form stays available and is what the canvas linter tells the
        // author to use (finding `dup-published-param`).
        //
        // Denylisted auth-bearing names are never aliased at all, ambiguous or not.
        foreach (var (paramKey, candidate) in shortNameCandidates)
        {
            if (candidate.ProducerCount != 1) continue;
            if (DenylistedShortParamNames.Contains(paramKey)) continue;
            if (variables.ContainsKey(paramKey)) continue;
            variables[paramKey] = candidate.Value;
        }

        return variables;
    }

    /// <summary>One candidate for the unqualified short-name alias, plus how many ancestors
    /// published that name. Only a count of exactly one earns the alias.</summary>
    private readonly record struct ShortNameCandidate(string Value, int ProducerCount);


    /// <summary>
    /// JSON string-escape used by template expansion. Escapes the full 0x00-0x1F control
    /// character range per RFC 8259, since any unescaped control character would land
    /// literally in the JSON body and break <see cref="JsonDocument.Parse"/>.
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
        IReadOnlyDictionary<string, string>? globalVariables = null,
        IReadOnlyDictionary<string, string>? manualParameters = null)
        => ResolveVariables(config, results, BuildOutputVariableAliasMap(allNodes), globalVariables, manualParameters);

    /// <summary>
    /// Same as <see cref="ResolveVariables(JsonElement, IReadOnlyDictionary{string,
    /// ActivityResult}, IReadOnlyDictionary{string, string}?, IReadOnlyDictionary{string,
    /// string}?)"/>,
    /// but skips substitution for the named top-level properties. Used by SQL/DB-trigger
    /// activities where <c>query</c> text is the wrong place for <c>{{var}}</c> expansion,
    /// since it would smuggle untrusted values into a raw <c>CommandText</c>. Those activities
    /// must bind dynamic values through <c>parameters</c> instead. Only top-level property
    /// names are honoured; nested objects under a non-protected key are still fully resolved.
    /// </summary>
    internal static JsonElement ResolveVariablesExcept(
        JsonElement config,
        IReadOnlyDictionary<string, ActivityResult> results,
        IReadOnlyDictionary<string, string>? outputVariableToStepId,
        IReadOnlyDictionary<string, string>? globalVariables,
        IReadOnlySet<string> doNotResolveFields,
        IReadOnlyDictionary<string, string>? manualParameters = null)
    {
        if (doNotResolveFields is null || doNotResolveFields.Count == 0
            || config.ValueKind != JsonValueKind.Object)
        {
            return ResolveVariables(config, results, outputVariableToStepId, globalVariables, manualParameters);
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
                var subResolved = ResolveVariables(prop.Value, results, outputVariableToStepId, globalVariables, manualParameters);
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
        IReadOnlyDictionary<string, string>? globalVariables = null,
        IReadOnlyDictionary<string, string>? manualParameters = null)
    {
        var configJson = config.GetRawText();
        if (string.IsNullOrEmpty(configJson) || !configJson.Contains("{{"))
            return config;

        var variableMap = BuildVariableMap(results, outputVariableToStepId);

        // First pass: {{globals.NAME}} — admin-managed shared constants. Runs before the
        // step-output pass so a global can be referenced in a literal that downstream
        // expansion consumes without a name-collision risk.
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

        // {{manual.NAME}} — the run's trigger inputs. Same shape as the globals pass: a flat
        // name lookup, JSON-escaped for the string context it sits in. An unknown name is left
        // verbatim so the unresolved-template check can fail the step with a real diagnostic
        // instead of the value silently rendering as its own placeholder.
        if (manualParameters is not null && manualParameters.Count > 0)
        {
            configJson = ManualPattern.Replace(configJson, match =>
            {
                var name = match.Groups[1].Value;
                if (!manualParameters.TryGetValue(name, out var val)) return match.Value;
                return JsonEscape(val);
            });
        }

        // Replace {{name.output}}, {{name.error}}, and {{name.param.paramName}} patterns.
        // [\w-]+ supports step IDs with hyphens like "step-1776111023799".
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

        // Dispose the JsonDocument so its pooled buffers return to the ArrayPool. Returning
        // RootElement directly would leak the buffer; Clone() detaches the element from the
        // pooled document's lifetime so the caller gets a self-contained, still-valid copy.
        using var doc = JsonDocument.Parse(resolved);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Shared between the JSON-config and the plain-string resolvers. Maps each previous step
    /// by its id and by its configured <c>outputVariable</c> alias (when set and non-equal).
    /// Uses OrdinalIgnoreCase so <c>{{Step}}</c> and <c>{{STEP}}</c> resolve to the same entry.
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

    internal static Dictionary<string, string> BuildOutputVariableAliasMap(IReadOnlyList<WorkflowNode> allNodes)
        => WorkflowDefinitionDocument.BuildOutputVariableAliasMap(allNodes);

    /// <summary>
    /// Resolves {{varName.output}} placeholders in a single string value (not JSON).
    /// Used for targetMachineId and credentialId fields.
    /// </summary>
    internal static string? ResolveStringValue(string? raw, IReadOnlyDictionary<string, ActivityResult> results, List<WorkflowNode> allNodes,
        IReadOnlyDictionary<string, string>? globalVariables = null,
        IReadOnlyDictionary<string, string>? manualParameters = null)
        => ResolveStringValue(raw, results, BuildOutputVariableAliasMap(allNodes), globalVariables, manualParameters);

    /// <summary>Hot-path overload taking a pre-built output-variable alias index.</summary>
    internal static string? ResolveStringValue(string? raw, IReadOnlyDictionary<string, ActivityResult> results,
        IReadOnlyDictionary<string, string>? outputVariableToStepId,
        IReadOnlyDictionary<string, string>? globalVariables = null,
        IReadOnlyDictionary<string, string>? manualParameters = null)
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

        if (manualParameters is not null && manualParameters.Count > 0)
        {
            raw = ManualPattern.Replace(raw!, m =>
                manualParameters.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
        }

        var variableMap = BuildVariableMap(results, outputVariableToStepId);

        return StepPattern.Replace(raw!, match =>
        {
            var varName = match.Groups[1].Value;
            var property = match.Groups[2].Value;

            if (!variableMap.TryGetValue(varName, out var result))
                return match.Value;

            if (property.StartsWith("param.") && match.Groups[3].Success)
            {
                // No Trim: keeps the value byte-identical to ResolveVariables' JSON-config
                // pass, so the same template resolves the same way in restApi.url (string-path)
                // and restApi.body (JSON-path).
                return result.OutputParameters.TryGetValue(match.Groups[3].Value, out var pv) ? pv : match.Value;
            }

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
