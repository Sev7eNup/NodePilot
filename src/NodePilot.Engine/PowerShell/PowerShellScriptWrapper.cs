using System.Text;
using Microsoft.Extensions.Logging;

namespace NodePilot.Engine.PowerShell;

internal static class PowerShellScriptWrapper
{
    public const string ParamsMarker = "###NODEPILOT_PARAMS###";

    /// <summary>
    /// Emitted to stdout by the wrapper's catch block when the user script raises a terminating
    /// PowerShell error. Lets the out-of-process engine determine "did the script throw?" WITHOUT
    /// relying on the process exit code — so an explicit `exit N` (which skips the catch) stays a
    /// non-failure, consistent with the runspace/WinRM `!HadErrors` rule. Stripped from Output by
    /// <c>PowerShellActivitySupport.ExtractMarkers</c>.
    /// </summary>
    public const string ErrorMarker = "###NODEPILOT_ERROR###";

    /// <summary>
    /// Emitted (always, on normal completion) followed by the captured <c>$LASTEXITCODE</c>.
    /// Kept separate from the user-variable PARAMS block so it never overrides a user-emitted
    /// marker nor forces an otherwise-empty PARAMS block. Lifted into <c>param.exitCode</c>.
    /// </summary>
    public const string ExitCodeMarker = "###NODEPILOT_EXITCODE###";

    public static string Wrap(string userScript, IReadOnlyDictionary<string, string> parameters, ILogger logger,
        IReadOnlyCollection<string>? outputCaptureAllowlist = null)
    {
        var scriptContent = new StringBuilder();

        // Wrap the entire body in & { ... } so user variables live in a child scope
        // that's discarded when the block exits. Without this, the runspace pool reuses
        // runspaces and variables from a prior run leak into the next: the snapshot
        // below would include them as "builtins", and the capture block would then
        // silently skip the user's freshly-assigned variables (their names match leaks).
        // Symptom: under concurrent load, runScript steps stop emitting param.* after
        // the runspace pool warms up — downstream edge conditions like
        // `isNotEmpty(step.param.hostName)` evaluate false and the workflow short-circuits.
        scriptContent.AppendLine("& {");
        scriptContent.AppendLine("$ErrorActionPreference = 'Stop'");
        scriptContent.AppendLine("$ProgressPreference = 'SilentlyContinue'");
        // Wrap the body in try/catch: on a terminating error, emit ErrorMarker (so the process
        // engine can detect a throw without the exit code) then re-throw (keeps runspace/WinRM
        // HadErrors). An explicit `exit N` skips both the capture block AND the catch — no marker,
        // no PARAMS — which is exactly how `exit N` stays a non-failure under the error-based rule.
        scriptContent.AppendLine("try {");

        // Snapshot built-in variables before injecting upstream parameters so the
        // capture block only exports values introduced by NodePilot and the user script.
        scriptContent.AppendLine("$__npBuiltinVars = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)");
        scriptContent.AppendLine("Get-Variable -Scope Local | ForEach-Object { $__npBuiltinVars.Add($_.Name) | Out-Null }");
        scriptContent.AppendLine("$__npBuiltinVars.Add('__npBuiltinVars') | Out-Null");
        scriptContent.AppendLine("$__npBuiltinVars.Add('Params') | Out-Null");

        // Inject parameters as both a lookup hashtable and short alias variables.
        scriptContent.AppendLine("$Params = @{}");
        foreach (var (key, value) in parameters)
        {
            var escaped = value.Replace("'", "''");
            var shortName = key.Contains('.') ? key[(key.LastIndexOf('.') + 1)..] : key;
            var safeName = shortName.Replace(".", "_").Replace("-", "_").Replace(" ", "_");

            if (!ParameterKeyValidator.IsValid(shortName))
            {
                logger.LogWarning(
                    "PowerShellScriptWrapper: skipping parameter '{Key}' - name does not match allow-list [A-Za-z0-9_]+.",
                    key);
                continue;
            }

            scriptContent.AppendLine($"$Params['{shortName}'] = '{escaped}'");
            if (!string.IsNullOrEmpty(safeName) && ParameterKeyValidator.IsValid(safeName))
                scriptContent.AppendLine($"if (-not $__npBuiltinVars.Contains('{safeName}')) {{ ${safeName} = '{escaped}' }}");
        }

        scriptContent.AppendLine("# === USER SCRIPT ===");
        scriptContent.AppendLine(userScript);
        scriptContent.AppendLine();

        // Use the IDictionary base-count via psbase to avoid collisions with user variables
        // like $count that create a hashtable entry named "count".
        scriptContent.AppendLine("# === NODEPILOT OUTPUT CAPTURE ===");
        // Capture $LASTEXITCODE (last native command's exit code; null when none ran) before any
        // capture cmdlet — Get-Variable / ConvertTo-Json are cmdlets, not native, so they don't
        // reset it. Surfaced as the reserved __npExitCode key → {{step.param.exitCode}}.
        scriptContent.AppendLine("$__npExit = if ($null -ne $LASTEXITCODE) { $LASTEXITCODE } else { 0 }");
        scriptContent.AppendLine("$__npOut = @{}");
        if (outputCaptureAllowlist is not null)
        {
            // Custom-activity capture: ONLY the declared output names are surfaced. This excludes
            // both the injected input variables and any helper locals the author created, fixing the
            // leak that an unrestricted sweep would otherwise produce (injected $name vars are not in
            // the pre-injection builtin snapshot). exitCode is still emitted by its own marker below.
            scriptContent.AppendLine("$__npOutAllow = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)");
            foreach (var name in outputCaptureAllowlist)
            {
                if (!ParameterKeyValidator.IsValid(name)) continue; // grammar already enforced on save; defensive
                scriptContent.AppendLine($"$__npOutAllow.Add('{name}') | Out-Null");
            }
            scriptContent.AppendLine("Get-Variable -Scope Local -ErrorAction SilentlyContinue | Where-Object {");
            scriptContent.AppendLine("    $__npOutAllow.Contains($_.Name)");
            scriptContent.AppendLine("} | ForEach-Object {");
            scriptContent.AppendLine("    $__npOut[$_.Name] = [string]$_.Value");
            scriptContent.AppendLine("}");
        }
        else
        {
            scriptContent.AppendLine("Get-Variable -Scope Local -ErrorAction SilentlyContinue | Where-Object {");
            scriptContent.AppendLine("    -not $__npBuiltinVars.Contains($_.Name) -and $_.Name -notlike '__np*' -and $_.Name -ne 'Params'");
            scriptContent.AppendLine("} | ForEach-Object {");
            scriptContent.AppendLine("    $__npOut[$_.Name] = [string]$_.Value");
            scriptContent.AppendLine("}");
        }
        scriptContent.AppendLine("if ($__npOut.psbase.Count -gt 0) {");
        scriptContent.AppendLine($"    Write-Output '{ParamsMarker}'");
        scriptContent.AppendLine("    Write-Output ($__npOut | ConvertTo-Json -Compress)");
        scriptContent.AppendLine("}");
        // Always surface the captured exit code as its OWN marker (separate from the user-variable
        // PARAMS block, so it never overrides a user-emitted marker nor forces an empty PARAMS).
        scriptContent.AppendLine($"Write-Output '{ExitCodeMarker}'");
        scriptContent.AppendLine("Write-Output ([string]$__npExit)");
        // Close try → on a thrown terminating error, mark it on stdout and re-throw; then close
        // the & { ... } scope wrapper opened at the top.
        scriptContent.AppendLine("}");
        scriptContent.AppendLine("catch {");
        scriptContent.AppendLine($"    Write-Output '{ErrorMarker}'");
        scriptContent.AppendLine("    throw");
        scriptContent.AppendLine("}");
        scriptContent.AppendLine("}");

        return scriptContent.ToString();
    }
}
