using System.Text.Json;
using System.Text.RegularExpressions;

namespace NodePilot.Api.Security;

/// <summary>
/// Static checks for dangerous PowerShell patterns in workflow definitions. The engine's
/// variable interpolation is safe as long as the value lands in a single-quoted literal,
/// but <c>Invoke-Expression</c>, the call operator <c>&amp; ($cmd)</c>, and
/// <c>$ExecutionContext.InvokeCommand</c> re-evaluate the resolved string as code — which
/// turns any attacker-controlled variable into RCE.
///
/// This is a <i>linter</i>, not a compiler: it returns structured warnings that the API can
/// surface on save. A workflow author who genuinely needs those constructs can add an
/// acknowledgement flag in the workflow metadata (future work).
/// </summary>
public static class WorkflowScriptLinter
{
    public sealed record Warning(string StepId, string Rule, string Message);

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly (string Rule, Regex Pattern, string Message)[] Rules =
    {
        ("invoke-expression",
            new Regex(@"\bInvoke-Expression\b|\biex\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout),
            "Invoke-Expression / iex re-evaluates the resolved string as code. " +
            "Template values like {{prev.output}} bypass the single-quote safety net here."),
        ("call-operator-on-variable",
            // & $cmd / & ($cmd) / & "path\$x" — the call operator fed from a template or
            // variable can be turned into RCE by an upstream output. Safe usages like
            // & 'C:\Tools\tool.exe' are not flagged.
            new Regex(@"^\s*&\s*\(?\s*\$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline, RegexTimeout),
            "Calling a variable with the & operator bypasses template quoting; rebind values " +
            "to explicit parameters instead of building the command string dynamically."),
        ("execution-context-invoke",
            new Regex(@"\$ExecutionContext\.InvokeCommand\.InvokeScript\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout),
            "$ExecutionContext.InvokeCommand.InvokeScript is equivalent to Invoke-Expression."),
    };

    /// <summary>
    /// Lints a single PowerShell script (e.g. a custom activity's <c>ScriptTemplate</c>, which lives
    /// outside any workflow node and so is never reached by <see cref="Lint"/>). Runs the same RCE
    /// pattern rules. <paramref name="stepId"/> labels the warnings (use the activity key/id).
    /// </summary>
    public static List<Warning> LintScript(string? script, string stepId = "")
    {
        var warnings = new List<Warning>();
        if (string.IsNullOrEmpty(script)) return warnings;
        foreach (var (rule, pattern, message) in Rules)
            if (pattern.IsMatch(script))
                warnings.Add(new Warning(stepId, rule, message));
        return warnings;
    }

    /// <summary>
    /// Lints every <c>script</c> property found in a workflow definition JSON.
    /// Returns an empty list when the JSON is malformed (the save endpoint does its own
    /// shape validation and will reject before reaching here in practice).
    /// </summary>
    public static List<Warning> Lint(string definitionJson)
    {
        var warnings = new List<Warning>();
        try
        {
            using var doc = JsonDocument.Parse(definitionJson);
            if (!doc.RootElement.TryGetProperty("nodes", out var nodes)) return warnings;

            foreach (var node in nodes.EnumerateArray())
            {
                var id = node.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "?" : "?";
                if (!node.TryGetProperty("data", out var data)) continue;
                if (!data.TryGetProperty("config", out var cfg) || cfg.ValueKind != JsonValueKind.Object) continue;
                if (!cfg.TryGetProperty("script", out var scriptEl) || scriptEl.ValueKind != JsonValueKind.String) continue;

                var script = scriptEl.GetString() ?? "";
                foreach (var (rule, pattern, message) in Rules)
                {
                    if (pattern.IsMatch(script))
                        warnings.Add(new Warning(id, rule, message));
                }
            }
        }
        catch (JsonException)
        {
            // Caller is expected to validate JSON shape separately; return nothing rather
            // than throw so a lint failure doesn't hide a better error.
        }
        return warnings;
    }
}
