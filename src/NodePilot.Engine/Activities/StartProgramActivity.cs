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
///   filePath           string, required, such as "C:\\Program Files\\7-Zip\\7z.exe"
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
/// Success to exitCode is in successExitCodes (or fire-and-forget successfully started the
/// process).
///   Output  -> stdout plus a short meta line (PID, ExitCode, Duration).
///   ErrorOutput -> stderr, or a timeout/launch error.
///   OutputParameters["exitCode"], ["processId"], ["stdout"], ["stderr"], ["waited"].
/// </summary>
public class StartProgramActivity : BaseRemoteActivity
{
    public override string ActivityType => "startProgram";

    private static readonly PowerShellOperationMarkers ResultMarkers = PowerShellOperation.Markers("PROGRAM");

    // Default kill timeout for wait mode, matching the documented catalog default.
    internal const int DefaultTimeoutSeconds = 300;

    // Cap stdout/stderr at 1,048,576 bytes each. Beyond this the buffers stop growing
    // but the pipes keep draining, so the producer doesn't block. Callers learn via
    // `OutputParameters["stdoutTruncated"|"stderrTruncated"]`.
    internal const int MaxOutputBytesPerStream = 1024 * 1024;

    // Fallback for Engine:IsolatedDrainGraceSeconds, matching PowerShellEngineFactory.
    internal const int DefaultDrainGraceSeconds = 5;

    // Configuration is inherited from the base class (protected `_configuration`);
    // no local copy is kept here.

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
        // so a missing config key is treated as "DisallowShellExecute=true" by default.
        // Dev/test deployments that need shell-mediated launches set
        // StartProgram:DisallowShellExecute=false explicitly.
        if (useShell)
        {
            var raw = _configuration["StartProgram:DisallowShellExecute"];
            var disallow = string.IsNullOrWhiteSpace(raw)
                || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
            if (disallow)
                throw new InvalidOperationException(
                    "StartProgram: useShellExecute=true is blocked by configuration. " +
                    "Either invoke the executable directly (useShellExecute=false) or set " +
                    "StartProgram:DisallowShellExecute=false (e.g. in appsettings.Development.json) " +
                    "to permit shell-mediated launches.");
        }
        var wait = config.GetBool("waitForExit", true);
        // Default is 300s, matching the documented default and the activity-catalog UI hint.
        // PowerShellOperation.TimeoutSecondsFromConfig returns null for missing or non-positive
        // values, so a wait-mode step never ends up waiting forever with no bound.
        // Explicit per-step values still override.
        var timeoutSeconds = PowerShellOperation.TimeoutSecondsFromConfig(config) ?? DefaultTimeoutSeconds;

        // Script is embedded with single-quoted PS strings. Uses the shared PowerShell Operation
        // module
        // so every activity builder funnels through the same apostrophe-doubling routine.
        var pFile = PowerShellOperation.Literal(filePath);
        var pArgs = PowerShellOperation.Literal(arguments);
        var pDir = PowerShellOperation.Literal(workingDir);
        // Process.WaitForExit(int) accepts -1 (Timeout.Infinite) as "wait indefinitely",
        // so a missing user-timeout maps cleanly to that without a separate code path.
        var timeoutMs = PowerShellOperation.ToWaitForExitMilliseconds(timeoutSeconds);
        // Once the process has exited, an unfinished pipe read means a grandchild inherited the
        // write handle (`cmd /c start …`), so it will never reach EOF. Bound that stretch by the
        // same short grace the isolated runScript path uses instead of the full step timeout.
        // 0 or negative falls back to the default: no bound would restore the hang, and a zero
        // grace would cut output short on every call.
        var configuredGrace = _configuration.GetValue<int?>("Engine:IsolatedDrainGraceSeconds");
        var drainGraceMs = (configuredGrace > 0 ? configuredGrace.Value : DefaultDrainGraceSeconds) * 1000;
        var useShellPs = useShell ? "$true" : "$false";
        var waitPs = wait ? "$true" : "$false";
        var targetPathGuard = TargetPathGuardScript.Build(
            _configuration,
            ("$__filePath", "filePath"),
            ("$__workingDir", "workingDirectory"));
        // The program gets the machine's module path, not the one the in-process SDK rewrote for
        // this host (see ChildProcessEnvironment). Left out with UseShellExecute: .NET rejects
        // environment variables there.
        var childModulePath = useShell
            ? ""
            : ChildProcessEnvironment.PowerShellFunction + "\n" + ChildProcessEnvironment.PowerShellApply("$psi");

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
            $__capture = -not $__useShell -and $__wait
            $__timeoutMs = {{timeoutMs}}
            $__drainGraceMs = {{drainGraceMs}}
            {{targetPathGuard}}

            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName = $__filePath
            if ($__arguments.Length -gt 0) { $psi.Arguments = $__arguments }
            if ($__workingDir.Length -gt 0) { $psi.WorkingDirectory = $__workingDir }
            $psi.UseShellExecute = $__useShell
            if ($__capture) {
                $psi.RedirectStandardOutput = $true
                $psi.RedirectStandardError = $true
            }
            # Output is read as bytes and decoded at the end: valid UTF-8 as UTF-8, anything else
            # in the OEM code page that console programs use by default.
            $__oem = [Console]::OutputEncoding
            try { $__oem = [System.Text.Encoding]::GetEncoding([System.Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage) } catch { }
            function __npDecode([System.IO.MemoryStream]$buffer, [bool]$truncated) {
                $bytes = $buffer.ToArray()
                if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
                    return [System.Text.Encoding]::Unicode.GetString($bytes, 2, $bytes.Length - 2)
                }
                $start = if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { 3 } else { 0 }
                $strict = New-Object System.Text.UTF8Encoding($false, $true)
                # A cut at the cap can split the last UTF-8 character; retry without it.
                $cuts = if ($truncated) { 0..3 } else { @(0) }
                foreach ($cut in $cuts) {
                    $length = $bytes.Length - $start - $cut
                    if ($length -lt 0) { break }
                    try { return $strict.GetString($bytes, $start, $length) } catch { }
                }
                return $__oem.GetString($bytes)
            }
            if (-not $__useShell) {
                $psi.CreateNoWindow = $true
            }
            {{childModulePath}}

            $proc = New-Object System.Diagnostics.Process
            $proc.StartInfo = $psi

            $stdoutBuf = New-Object System.IO.MemoryStream
            $stderrBuf = New-Object System.IO.MemoryStream
            $__npOutputCap = {{MaxOutputBytesPerStream}}
            $launchError = $null
            try {
                try {
                    [void]$proc.Start()
                } catch {
                    $launchError = $_.Exception.Message
                }
                if ($null -eq $launchError) {
                    $processId = $proc.Id
                    $exitCode = $null
                    $timedOut = $false
                    $drainIncomplete = $false
                    if ($__capture) {
                        # Read both pipes concurrently, preserving order within each stream.
                        $streams = @(
                            @{ Reader = $proc.StandardOutput.BaseStream; Buffer = [byte[]]::new(4096); Output = $stdoutBuf; Pending = $null },
                            @{ Reader = $proc.StandardError.BaseStream; Buffer = [byte[]]::new(4096); Output = $stderrBuf; Pending = $null }
                        )
                        foreach ($stream in $streams) {
                            $stream.Pending = $stream.Reader.ReadAsync($stream.Buffer, 0, $stream.Buffer.Length)
                        }
                        $drainClock = [System.Diagnostics.Stopwatch]::StartNew()
                        $exitObservedMs = $null
                        while ($true) {
                            foreach ($stream in $streams) {
                                if ($null -ne $stream.Pending -and $stream.Pending.IsCompleted) {
                                    $count = $stream.Pending.GetAwaiter().GetResult()
                                    if ($count -eq 0) {
                                        $stream.Pending = $null
                                    } else {
                                        # Keep draining after the cap so the child cannot block on a full pipe.
                                        $take = [Math]::Min($count, $__npOutputCap - $stream.Output.Length)
                                        if ($take -gt 0) { $stream.Output.Write($stream.Buffer, 0, $take) }
                                        $stream.Pending = $stream.Reader.ReadAsync($stream.Buffer, 0, $stream.Buffer.Length)
                                    }
                                }
                            }
                            $pending = @($streams | Where-Object { $null -ne $_.Pending } | ForEach-Object { $_.Pending })
                            if ($proc.HasExited -and $pending.Count -eq 0) { break }
                            if ($proc.HasExited) {
                                # The program is done; only a leaked write handle keeps the pipe
                                # open. Give up after the drain grace and keep what was buffered
                                # instead of failing a successful run on the step timeout.
                                if ($null -eq $exitObservedMs) { $exitObservedMs = $drainClock.ElapsedMilliseconds }
                                if (($drainClock.ElapsedMilliseconds - $exitObservedMs) -ge $__drainGraceMs) {
                                    $drainIncomplete = $true
                                    break
                                }
                            } elseif ($drainClock.ElapsedMilliseconds -ge $__timeoutMs) {
                                $timedOut = $true
                                break
                            }
                            if ($pending.Count -gt 0) {
                                [void][System.Threading.Tasks.Task]::WaitAny([System.Threading.Tasks.Task[]]$pending, 50)
                            } else {
                                [void]$proc.WaitForExit(50)
                            }
                        }
                    } elseif ($__wait) {
                        $timedOut = -not $proc.WaitForExit($__timeoutMs)
                    }
                    if ($timedOut) {
                        try { $proc.Kill() } catch {}
                        [void]$proc.WaitForExit(2000)
                    }
                    if ($__wait -and $proc.HasExited) {
                        $exitCode = $proc.ExitCode
                    }
                }
            } finally {
                try {
                    if ($__wait -and -not $proc.HasExited) {
                        $proc.Kill()
                        [void]$proc.WaitForExit(2000)
                    }
                } catch {}
                $proc.Dispose()
            }

            if ($null -ne $launchError) {
                $result = @{
                    Launched = $false
                    LaunchError = $launchError
                    Waited = $__wait
                }
            } else {
                $result = @{
                    Launched = $true
                    ProcessId = $processId
                    ExitCode = $exitCode
                    StdOut = __npDecode $stdoutBuf ($stdoutBuf.Length -ge $__npOutputCap)
                    StdErr = __npDecode $stderrBuf ($stderrBuf.Length -ge $__npOutputCap)
                    StdOutTruncated = ($stdoutBuf.Length -ge $__npOutputCap)
                    StdErrTruncated = ($stderrBuf.Length -ge $__npOutputCap)
                    Waited = $__wait
                    TimedOut = $timedOut
                    DrainIncomplete = $drainIncomplete
                }
            }

            {{ResultMarkers.RenderJsonEnvelope("$result", depth: 5)}}
            """;
    }

    /// <summary>
    /// A shell-executed program gets the launching process's environment, and .NET allows no
    /// environment of its own with UseShellExecute. In the pool that environment carries the
    /// module path the SDK rewrote, so this launch runs in a Windows PowerShell process, whose
    /// environment has the machine's module path (see ChildProcessEnvironment). That is also the
    /// PowerShell the script gets remotely. Everything else stays in the pool, where the script
    /// sets the child's module path itself.
    /// </summary>
    /// <summary>
    /// The script enforces the program timeout itself (kill, then the drain grace), so the run
    /// gets that much more time. Otherwise the transport stops the script before it can report
    /// the timeout with the program's partial output.
    /// </summary>
    protected override int? TransportTimeoutSeconds(JsonElement config)
    {
        var programTimeout = PowerShellOperation.TimeoutSecondsFromConfig(config) ?? DefaultTimeoutSeconds;
        var configuredGrace = _configuration.GetValue<int?>("Engine:IsolatedDrainGraceSeconds");
        var grace = configuredGrace > 0 ? configuredGrace.Value : DefaultDrainGraceSeconds;
        return programTimeout + grace + TransportTimeoutMarginSeconds;
    }

    // Covers session setup, process start and the two-second wait after a kill.
    internal const int TransportTimeoutMarginSeconds = 30;

    protected override IPowerShellExecutionEngine SelectLocalEngine(JsonElement config)
        => config.GetBool("useShellExecute", false)
            ? _engineFactory.GetEngine("auto")
            : base.SelectLocalEngine(config);

    protected override ActivityResult PostProcess(ActivityResult raw, JsonElement config)
    {
        // Engine returned a failure from the transport layer (WinRM down, script threw before
        // marker).
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
        // The program finished but a surviving child still holds the output pipe, so the capture
        // stopped at the drain grace. Say so — the buffered output may be short.
        if (parsed.DrainIncomplete == true)
            metaLine += " (output capture incomplete: a surviving child process holds the output pipe)";

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
            errorOutput = $"Process or output capture timed out. Partial stderr: {stdErr}";
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

    // Same comma-separated allow-list runScript parses, except that "unset" means {0} here
    // (a program's exit code is always gated) instead of "no gate at all".
    private static HashSet<int> ParseSuccessExitCodes(JsonElement config)
        => PowerShellActivitySupport.ParseSuccessExitCodes(config.GetStringOrNull("successExitCodes"))
           ?? new HashSet<int> { 0 };

    private void ValidateLocalAbsolutePath(string fieldName, string path)
    {
        try
        {
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
        public bool? DrainIncomplete { get; init; }
    }
}
