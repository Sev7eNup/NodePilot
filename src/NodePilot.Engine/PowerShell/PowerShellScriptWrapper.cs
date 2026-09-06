using System.Text;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Activities;

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

    /// <summary>
    /// Emitted before any other statement, so an out-of-process engine can tell "the script never
    /// ran" from "the script ran and exited". PowerShell parses a whole <c>-File</c> script before
    /// executing its first statement, so a syntax error — a terminating error under the documented
    /// contract — leaves stdout empty and the catch block unreached. Its absence is therefore the
    /// only reliable "did not execute" signal; <see cref="ExitCodeMarker"/> cannot serve, since a
    /// plain <c>exit N</c> skips it too and must stay a success.
    /// Stripped from Output by <c>PowerShellActivitySupport.ExtractMarkers</c>.
    /// </summary>
    public const string StartMarker = "###NODEPILOT_START###";

    /// <summary>
    /// Namespaces whose keys still earn a short alias. Names are unique inside each of them —
    /// two globals cannot share a name, nor can two trigger inputs — so flattening them cannot
    /// produce an owner-less variable.
    /// </summary>
    private static readonly string[] FlattenablePrefixes = ["manual.", "globals."];

    /// <summary>
    /// The short name a key may be bound to, or false when the key is step-scoped
    /// (<c>step.param.x</c>, <c>step.output</c>, …).
    ///
    /// <para>Step-scoped keys are deliberately not flattened. Deriving <c>$hostName</c> from
    /// <c>stepA.param.hostName</c> AND <c>stepB.param.hostName</c> made the last one written win,
    /// which is dictionary order, not a decision. <see cref="Execution.VariableResolver"/> owns
    /// that decision: it supplies an unqualified entry only when exactly one activity publishes
    /// the name, and that entry is what binds the alias here.</para>
    /// </summary>
    private static bool TryGetAliasName(string key, out string shortName)
    {
        foreach (var prefix in FlattenablePrefixes)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                shortName = key[prefix.Length..];
                return shortName.Length > 0 && !shortName.Contains('.');
            }
        }

        shortName = key;
        return !key.Contains('.');
    }

    public static string Wrap(string userScript, IReadOnlyDictionary<string, string> parameters, ILogger logger,
        IReadOnlyCollection<string>? outputCaptureAllowlist = null)
    {
        var scriptContent = new StringBuilder();

        // A short name can still be claimed twice across namespaces — a global and a trigger
        // input both called FOO. Bind neither rather than let insertion order decide.
        var claims = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in parameters.Keys)
        {
            if (!TryGetAliasName(key, out var candidate)) continue;
            claims[candidate] = claims.TryGetValue(candidate, out var n) ? n + 1 : 1;
        }
        var ambiguousShortNames = claims.Where(kv => kv.Value > 1)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Two nested scopes, and the nesting is what makes the output capture correct.
        //
        // OUTER holds the injected upstream parameters. INNER runs the user script and is what
        // the capture block enumerates. A PowerShell assignment inside a child scope creates a
        // NEW local variable instead of writing through to the parent, so
        // `Get-Variable -Scope Local` in the inner scope yields exactly "what this script
        // assigned" — an injected parameter the script only reads never appears, one it
        // reassigns appears with the new value. No value comparison is involved, so a
        // case-only edit or a deliberate re-assignment of the same value is still exported.
        //
        // Without this split, injection and capture shared one scope: every upstream parameter
        // came back out as this step's own output, and because those outputs are re-injected
        // downstream the set grew with every step in the chain.
        //
        // The outer scope is discarded when the invocation ends, so the aliases never leak into
        // the pooled runspace.
        // First statement in the file: proves execution began. See StartMarker.
        scriptContent.AppendLine($"Write-Output '{StartMarker}'");
        scriptContent.AppendLine("& {");
        scriptContent.AppendLine("$ErrorActionPreference = 'Stop'");
        scriptContent.AppendLine("$ProgressPreference = 'SilentlyContinue'");

        // Reserved names = what this scope already holds (covers whatever the host's
        // InitialSessionState defines) plus the explicit contract in
        // NodePilot.Core.Activities.PowerShellReservedVariables, which carries the preference
        // variables and the automatics a scope snapshot cannot see.
        scriptContent.AppendLine("$__npReserved = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)");
        scriptContent.AppendLine("Get-Variable -Scope Local | ForEach-Object { $__npReserved.Add($_.Name) | Out-Null }");
        foreach (var reserved in PowerShellReservedVariables.All)
            scriptContent.AppendLine($"$__npReserved.Add('{reserved}') | Out-Null");
        scriptContent.AppendLine("$__npReserved.Add('Params') | Out-Null");

        // Inject parameters as both a lookup hashtable and short alias variables.
        scriptContent.AppendLine("$Params = @{}");
        foreach (var (key, value) in parameters)
        {
            var escaped = value.Replace("'", "''");

            if (!TryGetAliasName(key, out var shortName))
            {
                // Step-scoped: reachable under its owner's full name, never as a bare variable.
                // `$Params['stepA.param.hostName']` is unambiguous by construction, which is the
                // point — two activities publishing `hostName` keep separate entries.
                if (ParameterKeyValidator.IsValidQualified(key))
                    scriptContent.AppendLine($"$Params['{key}'] = '{escaped}'");
                continue;
            }

            var safeName = shortName.Replace(".", "_").Replace("-", "_").Replace(" ", "_");

            if (!ParameterKeyValidator.IsValid(shortName))
            {
                logger.LogWarning(
                    "PowerShellScriptWrapper: skipping parameter '{Key}' - name does not match allow-list [A-Za-z0-9_]+.",
                    key);
                continue;
            }

            if (ambiguousShortNames.Contains(shortName))
            {
                // Two sources claim the same short name. Picking one would depend on dictionary
                // order, so neither is bound and the author references the value by its owner:
                // {{stepA.param.hostName}} in the script text, resolved before this wrapper runs.
                logger.LogWarning(
                    "PowerShellScriptWrapper: '{Key}' is not exposed as ${SafeName} - more than one source publishes that name. Reference it qualified instead.",
                    key, safeName);
                continue;
            }

            scriptContent.AppendLine($"$Params['{shortName}'] = '{escaped}'");

            // The wrapper's own variables are addressable by an upstream key: the key grammar
            // is [A-Za-z0-9_]+, so `__npReserved` is a legal parameter name. Binding it would
            // overwrite a HashSet with a string and take the wrapper down with a terminating
            // error, so the whole internal prefix is withheld from aliasing.
            if (safeName.StartsWith(PowerShellReservedVariables.InternalPrefix, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "PowerShellScriptWrapper: parameter '{Key}' is not exposed as ${SafeName} - the '{Prefix}' prefix is reserved for the wrapper. Use $Params['{ShortName}'].",
                    key, safeName, PowerShellReservedVariables.InternalPrefix, shortName);
                continue;
            }

            if (!string.IsNullOrEmpty(safeName) && ParameterKeyValidator.IsValid(safeName))
                scriptContent.AppendLine($"if (-not $__npReserved.Contains('{safeName}')) {{ ${safeName} = '{escaped}' }}");
        }

        // $LASTEXITCODE and $Error live in the runspace's GLOBAL session state, which a child
        // scope does not isolate, and pool runspaces are never recycled. Left alone, a step
        // reports the exit code of a native command from an unrelated earlier run — and
        // successExitCodes then gates on that foreign value. Reset here so both mean "produced
        // by this script".
        scriptContent.AppendLine("$global:LASTEXITCODE = $null");
        scriptContent.AppendLine("if ($null -ne $global:Error) { $global:Error.Clear() }");

        // try/catch stays in the OUTER scope: on a terminating error emit ErrorMarker (so the
        // process engine can detect a throw without the exit code) then re-throw (keeps
        // runspace/WinRM HadErrors). An explicit `exit N` skips both the capture block AND the
        // catch — no marker, no PARAMS — which is how `exit N` stays a non-failure under the
        // error-based rule.
        scriptContent.AppendLine("try {");
        scriptContent.AppendLine("& {");

        // Snapshot what the inner scope holds before the user script runs, so the capture block
        // exports only what the script itself introduced.
        scriptContent.AppendLine("$__npBuiltinVars = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)");
        scriptContent.AppendLine("Get-Variable -Scope Local | ForEach-Object { $__npBuiltinVars.Add($_.Name) | Out-Null }");

        scriptContent.AppendLine("# === USER SCRIPT ===");
        scriptContent.AppendLine(userScript);
        scriptContent.AppendLine();

        // Use the IDictionary base-count via psbase to avoid collisions with user variables
        // like $count that create a hashtable entry named "count".
        scriptContent.AppendLine("# === NODEPILOT OUTPUT CAPTURE ===");
        // Capture $LASTEXITCODE (last native command's exit code; null when none ran) before any
        // capture cmdlet — Get-Variable / ConvertTo-Json are cmdlets, not native, so they don't
        // reset it. Surfaced as the reserved __npExitCode key -> {{step.param.exitCode}}.
        scriptContent.AppendLine("$__npExit = if ($null -ne $LASTEXITCODE) { $LASTEXITCODE } else { 0 }");
        scriptContent.AppendLine("$__npOut = @{}");
        if (outputCaptureAllowlist is not null)
        {
            // Custom-activity capture: ONLY the declared output names are surfaced. This excludes
            // both the injected input variables and any helper locals the author created.
            // exitCode is still emitted by its own marker below.
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
            // $__npReserved is read from the parent scope. It carries the automatics that only
            // come into being while the script runs ($_, $foreach, $Matches, …) and are therefore
            // absent from the snapshot taken a few lines up.
            scriptContent.AppendLine("Get-Variable -Scope Local -ErrorAction SilentlyContinue | Where-Object {");
            scriptContent.AppendLine("    -not $__npBuiltinVars.Contains($_.Name) -and -not $__npReserved.Contains($_.Name) -and $_.Name -notlike '__np*' -and $_.Name -ne 'Params'");
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
        // Close the inner user scope, then the try, then the outer injection scope.
        scriptContent.AppendLine("}");
        scriptContent.AppendLine("}");
        scriptContent.AppendLine("catch {");
        scriptContent.AppendLine($"    Write-Output '{ErrorMarker}'");
        scriptContent.AppendLine("    throw");
        scriptContent.AppendLine("}");
        scriptContent.AppendLine("}");

        return scriptContent.ToString();
    }
}
