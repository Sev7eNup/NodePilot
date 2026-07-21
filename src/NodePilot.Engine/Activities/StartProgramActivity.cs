using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Remote "Run Program" — the equivalent of SCOrch's Start Program. Launches an external
/// program on the target machine (via WinRM / PowerShell, or locally via the localhost bypass)
/// and returns the exit code plus stdout/stderr.
///
/// Config:
///   filePath           string, required — e.g. "C:\\Program Files\\7-Zip\\7z.exe", "powershell.exe",
///                                            or a document-associated path like "C:\\report.xlsx"
///                                            (in which case set `useShellExecute=true`).
///   arguments          string, optional — command-line args, passed through as-is.
///   workingDirectory   string, optional — start directory.
///   useShellExecute    bool,   default false — true = launch via the OS shell (file
///                                             associations, UI apps); false = launch directly
///                                             with stdout/stderr capture.
///   waitForExit        bool,   default true  — false = fire-and-forget, only the PID is returned.
///   timeoutSeconds     int,    default 300   — kill timeout for wait mode.
///   successExitCodes   string, default "0"   — comma-separated list of accepted exit codes,
///                                             e.g. "0,1,2". Success=false on a mismatch.
///
/// Result:
///   Success → exitCode is in successExitCodes (or fire-and-forget successfully started the process).
///   Output  → stdout plus a short meta line (PID, ExitCode, Duration).
///   ErrorOutput → stderr, or a timeout/launch error.
///   OutputParameters["exitCode"], ["processId"], ["stdout"], ["stderr"], ["waited"].
/// </summary>
public class StartProgramActivity : BaseRemoteActivity
{
    public override string ActivityType => "startProgram";

    private static readonly PowerShellOperationMarkers ResultMarkers = PowerShellOperation.Markers("PROGRAM");

    // C2: documented in the catalog as 300s; the code previously fell through to
    // Timeout.Infinite when the field was missing.
    internal const int DefaultTimeoutSeconds = 300;

    // D7: cap stdout/stderr at 1 MiB each. Beyond this the StringBuilder stops
    // accepting new lines but the pipe keeps draining, so the producer doesn't
    // block. Caller learns via `OutputParameters["stdoutTruncated"|"stderrTruncated"]`.
    internal const int MaxOutputBytesPerStream = 1024 * 1024;

    // Configuration is inherited from the base class (protected `_configuration`). The local
    // copy used to duplicate that state and was removed once the base field became protected —
    // this also removes the compiler's redundant CS0108 hide-warning that came with it.

    public StartProgramActivity(
        IRemoteSessionFactory sessionFactory,
        ICredentialStore credentialStore,
        NodePilot.Data.NodePilotDbContext db,
        PowerShellEngineFactory engineFactory,
        IConfiguration configuration)
        : base(sessionFactory, credentialStore, db, engineFactory, configuration) { }

    protected override string BuildScript(JsonElement config, StepExecutionContext context)
    {
        var filePath = config.GetStringOrNull("filePath");
        if (string.IsNullOrWhiteSpace(filePath))
            throw new InvalidOperationException("StartProgram: 'filePath' is required");

        var arguments = config.GetString("arguments", "");
        var workingDir = config.GetString("workingDirectory", "");
        var useShell = config.GetBool("useShellExecute", false);

        ValidateLocalAbsolutePath("filePath", filePath);
        if (!string.IsNullOrWhiteSpace(workingDir))
            ValidateLocalAbsolutePath("workingDirectory", workingDir);

        // UseShellExecute=true spawns via the OS shell (document associations, UI apps).
        // The shell parser introduces a second injection surface beyond PowerShell quoting,
        // so default-on since Phase 3: a missing config key is treated as
        // "DisallowShellExecute=true". Dev/test deployments that need shell-mediated launches
        // flip StartProgram:DisallowShellExecute=false explicitly. Activities running with a
        // null configuration (test harness without IConfiguration) keep the old permissive
        // behaviour — there's no operator at risk in that scenario.
        if (useShell)
        {
            var raw = _configuration?["StartProgram:DisallowShellExecute"];
            var disallow = _configuration is not null
                && (string.IsNullOrWhiteSpace(raw)
                    || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase));
            if (disallow)
                throw new InvalidOperationException(
                    "StartProgram: useShellExecute=true is blocked by configuration. " +
                    "Either invoke the executable directly (useShellExecute=false) or set " +
                    "StartProgram:DisallowShellExecute=false (e.g. in appsettings.Development.json) " +
                    "to permit shell-mediated launches.");
        }
        var wait = config.GetBool("waitForExit", true);
        // C2: default 300s (matches the documented default + the activity-catalog UI
        // hint). PowerShellOperation.TimeoutSecondsFromConfig returns null for missing
        // or non-positive values, which previously fell through to Timeout.Infinite and
        // hung wait-mode steps forever. A sane bounded default is the safer operational
        // choice; explicit per-step values still override.
        var timeoutSeconds = PowerShellOperation.TimeoutSecondsFromConfig(config) ?? DefaultTimeoutSeconds;

        // Script is embedded with single-quoted PS strings. Uses the shared PowerShell Operation module
        // so every activity builder funnels through the same apostrophe-doubling routine.
        var pFile = PowerShellOperation.Literal(filePath);
        var pArgs = PowerShellOperation.Literal(arguments);
        var pDir = PowerShellOperation.Literal(workingDir);
        // Process.WaitForExit(int) accepts -1 (Timeout.Infinite) as "wait indefinitely",
        // so a missing user-timeout maps cleanly to that without a separate code path.
        var timeoutMs = PowerShellOperation.ToWaitForExitMilliseconds(timeoutSeconds);
        var useShellPs = useShell ? "$true" : "$false";
        var waitPs = wait ? "$true" : "$false";

        // Build a self-contained script that emits a JSON result block between markers.
        // Uses ProcessStartInfo directly for reliable stdout/stderr capture (Start-Process
        // has various quirks around -Wait + redirect combinations).
        return $$"""
            $ErrorActionPreference = 'Stop'
            $__filePath = {{pFile}}
            $__arguments = {{pArgs}}
            $__workingDir = {{pDir}}
            $__useShell = {{useShellPs}}
            $__wait = {{waitPs}}
            $__timeoutMs = {{timeoutMs}}

            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName = $__filePath
            if ($__arguments.Length -gt 0) { $psi.Arguments = $__arguments }
            if ($__workingDir.Length -gt 0) { $psi.WorkingDirectory = $__workingDir }
            $psi.UseShellExecute = $__useShell
            if (-not $__useShell) {
                $psi.RedirectStandardOutput = $true
                $psi.RedirectStandardError = $true
                $psi.CreateNoWindow = $true
            }

            $proc = New-Object System.Diagnostics.Process
            $proc.StartInfo = $psi

            $stdoutBuf = New-Object System.Text.StringBuilder
            $stderrBuf = New-Object System.Text.StringBuilder
            $__npOutputCap = {{MaxOutputBytesPerStream}}
            $registered = @()
            if (-not $__useShell) {
                # D7: cap each stream so a chatty process (npm install -verbose, robocopy /v, …)
                # cannot pin the PS host's managed heap. Once the StringBuilder reaches the cap
                # we silently drop subsequent lines but keep draining the pipe so the producer
                # doesn't block on a full buffer. Truncation is detected post-hoc by comparing
                # the builder length to the cap.
                $registered += Register-ObjectEvent -InputObject $proc -EventName OutputDataReceived -MessageData @{ Buf = $stdoutBuf; Cap = $__npOutputCap } -Action {
                    if ($null -ne $EventArgs.Data -and $Event.MessageData.Buf.Length -lt $Event.MessageData.Cap) {
                        [void]$Event.MessageData.Buf.AppendLine($EventArgs.Data)
                    }
                }
                $registered += Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived -MessageData @{ Buf = $stderrBuf; Cap = $__npOutputCap } -Action {
                    if ($null -ne $EventArgs.Data -and $Event.MessageData.Buf.Length -lt $Event.MessageData.Cap) {
                        [void]$Event.MessageData.Buf.AppendLine($EventArgs.Data)
                    }
                }
            }

            $launchError = $null
            try {
                [void]$proc.Start()
            } catch {
                $launchError = $_.Exception.Message
            }

            if ($null -ne $launchError) {
                $result = @{
                    Launched = $false
                    LaunchError = $launchError
                    Waited = $__wait
                }
            } else {
                $processId = $proc.Id
                if (-not $__useShell) {
                    $proc.BeginOutputReadLine()
                    $proc.BeginErrorReadLine()
                }

                $exitCode = $null
                $timedOut = $false
                if ($__wait) {
                    $exited = $proc.WaitForExit($__timeoutMs)
                    if (-not $exited) {
                        $timedOut = $true
                        try { $proc.Kill() } catch {}
                        $proc.WaitForExit(2000) | Out-Null
                    } else {
                        # Drain any async output readers
                        $proc.WaitForExit()
                    }
                    $exitCode = $proc.ExitCode
                }

                # Unregister events and flush buffers
                foreach ($r in $registered) {
                    try { Unregister-Event -SourceIdentifier $r.Name -ErrorAction SilentlyContinue } catch {}
                }

                $result = @{
                    Launched = $true
                    ProcessId = $processId
                    ExitCode = $exitCode
                    StdOut = $stdoutBuf.ToString()
                    StdErr = $stderrBuf.ToString()
                    StdOutTruncated = ($stdoutBuf.Length -ge $__npOutputCap)
                    StdErrTruncated = ($stderrBuf.Length -ge $__npOutputCap)
                    Waited = $__wait
                    TimedOut = $timedOut
                }
            }

            {{ResultMarkers.RenderJsonEnvelope("$result", depth: 5)}}
            """;
    }

    protected override ActivityResult PostProcess(ActivityResult raw, JsonElement config)
    {
        // Engine returned a failure from the transport layer (WinRM down, script threw before marker).
        // Pass that through but still try to parse if output is present.
        var output = raw.Output ?? "";
        if (!PowerShellOperation.TryExtractJsonBlock(output, ResultMarkers, out var block))
        {
            // No structured result — likely script failed before reaching Write-Output marker.
            return raw;
        }

        if (!PowerShellOperation.TryDeserializeJson(block.Json, out ProgramResult? parsed, out var parseError))
        {
            return new ActivityResult
            {
                Success = false,
                Output = raw.Output,
                ErrorOutput = $"StartProgram: could not parse result JSON: {parseError}",
                Duration = raw.Duration,
            };
        }

        if (parsed is null || parsed.Launched != true)
        {
            return new ActivityResult
            {
                Success = false,
                Output = null,
                ErrorOutput = $"StartProgram: launch failed — {parsed?.LaunchError ?? "unknown error"}",
                Duration = raw.Duration,
            };
        }

        // Strip the marker block from the visible output; keep stdout plus a meta line.
        var before = block.LeadingOutput;
        var stdOut = parsed.StdOut ?? "";
        var stdErr = parsed.StdErr ?? "";
        var pid = parsed.ProcessId?.ToString() ?? "";
        var exit = parsed.ExitCode?.ToString() ?? "(not waited)";
        var durMs = raw.Duration.TotalMilliseconds.ToString("F0");
        var metaLine = parsed.Waited == true
            ? $"[startProgram] PID={pid} ExitCode={exit} Duration={durMs}ms"
            : $"[startProgram] PID={pid} (fire-and-forget, not waited) Duration={durMs}ms";

        var display = string.IsNullOrWhiteSpace(stdOut)
            ? metaLine
            : metaLine + Environment.NewLine + stdOut.TrimEnd();
        if (!string.IsNullOrEmpty(before))
            display = before + Environment.NewLine + display;

        // Success semantics
        var successExitCodes = ParseSuccessExitCodes(config);
        bool success;
        string? errorOutput = null;
        if (parsed.TimedOut == true)
        {
            success = false;
            errorOutput = $"Process timed out and was killed. Partial stderr: {stdErr}";
        }
        else if (parsed.Waited != true)
        {
            // fire-and-forget — consider it launched successfully
            success = true;
        }
        else if (parsed.ExitCode is int code && successExitCodes.Contains(code))
        {
            success = true;
            if (!string.IsNullOrEmpty(stdErr)) errorOutput = stdErr; // surface warnings
        }
        else
        {
            success = false;
            errorOutput = string.IsNullOrEmpty(stdErr)
                ? $"Process exited with code {exit} (expected {string.Join(",", successExitCodes)})."
                : stdErr;
        }

        return new ActivityResult
        {
            Success = success,
            Output = display,
            ErrorOutput = errorOutput,
            Duration = raw.Duration,
            OutputParameters = new Dictionary<string, string>
            {
                ["exitCode"] = exit,
                ["processId"] = pid,
                ["stdout"] = stdOut,
                ["stderr"] = stdErr,
                ["waited"] = parsed.Waited == true ? "true" : "false",
                ["stdoutTruncated"] = parsed.StdOutTruncated == true ? "true" : "false",
                ["stderrTruncated"] = parsed.StdErrTruncated == true ? "true" : "false",
            },
        };
    }

    private static HashSet<int> ParseSuccessExitCodes(JsonElement config)
    {
        var raw = config.GetStringOrNull("successExitCodes");
        if (string.IsNullOrWhiteSpace(raw)) return new HashSet<int> { 0 };
        var set = new HashSet<int>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out var n)) set.Add(n);
        }
        return set.Count == 0 ? new HashSet<int> { 0 } : set;
    }

    private void ValidateLocalAbsolutePath(string fieldName, string path)
    {
        try
        {
            if (_configuration is not null)
                PathGuard.Validate(_configuration, path, allowWildcards: false);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"StartProgram: {fieldName} denied: {ex.Message}", ex);
        }

        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException($"StartProgram: {fieldName} must be an absolute local path");
    }

    private sealed class ProgramResult
    {
        public bool? Launched { get; init; }
        public string? LaunchError { get; init; }
        public int? ProcessId { get; init; }
        public int? ExitCode { get; init; }
        public string? StdOut { get; init; }
        public string? StdErr { get; init; }
        public bool? StdOutTruncated { get; init; }
        public bool? StdErrTruncated { get; init; }
        public bool? Waited { get; init; }
        public bool? TimedOut { get; init; }
    }
}
