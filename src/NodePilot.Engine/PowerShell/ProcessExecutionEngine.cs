using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NodePilot.Engine.PowerShell;

/// <summary>
/// Executes PowerShell scripts via an external process (pwsh.exe or powershell.exe).
/// This gives full access to all system-installed modules (AD, Exchange, SCCM, etc.)
/// and behaves identically to running a .ps1 file from the command line or ISE.
/// </summary>
public class ProcessExecutionEngine : IPowerShellExecutionEngine
{
    private readonly string _executable;
    private readonly ILogger _logger;

    // Grace period for the isolated stdout/stderr drain AFTER the root process has exited and its
    // job tree has been terminated. At that point no legitimate writer to the pipe remains, so an
    // unbounded wait can only be blocked by a leaked inherited pipe handle in an unrelated process
    // (see ProcessSpawnCoordinator). Bounding the drain converts that permanent hang into an
    // at-most-grace wait, so the isolated step always returns. Configurable via
    // Engine:IsolatedDrainGraceSeconds.
    private readonly TimeSpan _isolatedDrainGrace;

    public string EngineType { get; }
    public bool IsAvailable { get; }

    // internal (not private) so tests can construct an engine pointing at a deliberately invalid
    // executable to exercise the isolated native-failure catch path, and inject a tiny drain grace
    // to exercise the leaked-handle drain-timeout path fast. InternalsVisibleTo grants
    // NodePilot.Engine.Tests access; production still uses the CreatePwsh/CreateWindowsPowerShell
    // factory methods.
    internal ProcessExecutionEngine(string engineType, string executable, bool available, ILogger logger,
        TimeSpan? isolatedDrainGrace = null)
    {
        EngineType = engineType;
        _executable = executable;
        IsAvailable = available;
        _logger = logger;
        _isolatedDrainGrace = isolatedDrainGrace is { } g && g > TimeSpan.Zero ? g : TimeSpan.FromSeconds(5);
    }

    public static ProcessExecutionEngine CreatePwsh(ILogger logger, TimeSpan? isolatedDrainGrace = null)
    {
        var path = FindExecutable("pwsh.exe", "pwsh");
        return new ProcessExecutionEngine("pwsh", path ?? "pwsh.exe", path is not null, logger, isolatedDrainGrace);
    }

    public static ProcessExecutionEngine CreateWindowsPowerShell(ILogger logger, TimeSpan? isolatedDrainGrace = null)
    {
        var path = FindExecutable("powershell.exe", "powershell");
        return new ProcessExecutionEngine("powershell", path ?? "powershell.exe", path is not null, logger, isolatedDrainGrace);
    }

    public async Task<PowerShellExecutionResult> ExecuteAsync(PowerShellExecutionRequest request, CancellationToken ct)
    {
        if (request.Isolated)
        {
            // Isolation needs a Windows Job Object; production is always Windows, this guard is
            // only for cross-platform dev/CI compilation.
            if (!OperatingSystem.IsWindows())
                return new PowerShellExecutionResult
                {
                    Success = false,
                    ExitCode = -1,
                    Error = "Process isolation is only available on Windows.",
                };
            return await ExecuteIsolatedWindowsAsync(request, ct);
        }

        var sw = Stopwatch.StartNew();
        string? tempScript = null;

        try
        {
            tempScript = Path.Combine(Path.GetTempPath(), $"nodepilot_{Guid.NewGuid():N}.ps1");
            var wrappedScript = PowerShellScriptWrapper.Wrap(request.ScriptText, request.Parameters, _logger, request.OutputCaptureAllowlist);

            await WritePrivateScriptAsync(tempScript, wrappedScript, ct);

            var psi = new ProcessStartInfo
            {
                FileName = _executable,
                Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempScript}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = request.WorkingDirectory ?? Path.GetTempPath(),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var process = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

            _logger.LogDebug("Starting {Engine}: {File}", EngineType, tempScript);
            // Serialize with the isolated launcher's inheritable-handle window: this redirected
            // Process.Start uses bInheritHandles:true with no HANDLE_LIST, so without the gate it
            // could inherit a concurrent isolated launch's pipe write-handles and wedge that run.
            // See ProcessSpawnCoordinator. Only Start() needs the lock — the reads run after.
            lock (ProcessSpawnCoordinator.Gate)
            {
                process.Start();
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (request.Timeout is { } configuredTimeout)
                cts.CancelAfter(configuredTimeout);

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort: process may have exited */ }

                sw.Stop();
                var isUserCancel = ct.IsCancellationRequested;
                return new PowerShellExecutionResult
                {
                    Success = false,
                    ExitCode = -1,
                    Output = stdout.ToString().TrimEnd(),
                    Error = isUserCancel
                        ? "Script execution cancelled"
                        : $"Script execution timed out after {request.Timeout!.Value.TotalSeconds:0}s",
                    TimedOut = !isUserCancel,
                    Duration = sw.Elapsed,
                };
            }

            sw.Stop();
            var stdoutText = stdout.ToString();
            return new PowerShellExecutionResult
            {
                // Error-based success: the script "failed" only if it raised a terminating error
                // (the wrapper emits ErrorMarker on a throw). An explicit `exit N` is NOT a failure
                // — consistent with the in-process runspace and WinRM (!HadErrors). The real exit
                // code is still surfaced via ExitCode for {{step.param.exitCode}} / successExitCodes.
                Success = !stdoutText.Contains(PowerShellScriptWrapper.ErrorMarker, StringComparison.Ordinal),
                ExitCode = process.ExitCode,
                Output = stdoutText.TrimEnd(),
                Error = stderr.ToString().TrimEnd(),
                TimedOut = false,
                Duration = sw.Elapsed,
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new PowerShellExecutionResult
            {
                Success = false,
                ExitCode = -1,
                Error = $"Failed to start {_executable}: {ex.Message}",
                Duration = sw.Elapsed,
            };
        }
        finally
        {
            if (tempScript is not null)
            {
                try { File.Delete(tempScript); } catch { /* best-effort: temp script may already be gone */ }
            }
        }
    }

    /// <summary>
    /// Isolated execution path: launches the script in a Windows Job Object via
    /// <see cref="IsolatedProcessLauncher"/> (race-free job-at-creation), reads stdout/stderr
    /// concurrently with the exit wait, and reaps the whole tree on step end.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private async Task<PowerShellExecutionResult> ExecuteIsolatedWindowsAsync(PowerShellExecutionRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string? tempScript = null;

        try
        {
            tempScript = Path.Combine(Path.GetTempPath(), $"nodepilot_{Guid.NewGuid():N}.ps1");
            var wrappedScript = PowerShellScriptWrapper.Wrap(request.ScriptText, request.Parameters, _logger, request.OutputCaptureAllowlist);
            await WritePrivateScriptAsync(tempScript, wrappedScript, ct);

            var args = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempScript}\"";

            _logger.LogDebug("Starting isolated {Engine}: {File}", EngineType, tempScript);
            using var launched = IsolatedProcessLauncher.Launch(
                _executable, args, request.WorkingDirectory ?? Path.GetTempPath(), request.IsolationLimits);

            // Start draining the pipes IMMEDIATELY and concurrently with the wait — a noisy script
            // would otherwise deadlock once the pipe buffers fill (child blocks writing, we block
            // on exit). Mirrors BeginOutputReadLine on the non-isolated path.
            var stdoutTask = launched.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = launched.StandardError.ReadToEndAsync(ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (request.Timeout is { } configuredTimeout)
                cts.CancelAfter(configuredTimeout);

            var timedOut = false;
            var userCancel = false;
            try
            {
                await launched.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                userCancel = ct.IsCancellationRequested;
                timedOut = !userCancel;
            }

            // Step-end semantics: the step ends when the ROOT process exits (or we time out /
            // cancel). TerminateJobObject reaps any surviving grandchildren — crucially this also
            // closes their inherited pipe write-ends, so the read tasks reach EOF instead of
            // hanging on a child that outlived the root (e.g. a Start-Process background process).
            launched.Terminate();

            string stdout;
            string stderr;
            try
            {
                var drain = await DrainReadsAsync(stdoutTask, stderrTask, _isolatedDrainGrace, ct);
                stdout = drain.Stdout;
                stderr = drain.Stderr;
                if (drain.DrainTimedOut)
                {
                    // Leaked inherited pipe handle: the root is long dead and the job tree terminated,
                    // yet the write-end is still open in some other process (see ProcessSpawnCoordinator).
                    // We returned captured output and abandoned the drain rather than hang forever.
                    _logger.LogWarning(
                        "Isolated {Engine} (pid {Pid}): stdout/stderr did not reach EOF within {Grace:0}s after the " +
                        "root process exited and its job tree was terminated — a leaked inherited pipe handle in another " +
                        "process is holding the write-end open. Returned captured output and abandoned the drain.",
                        EngineType, launched.ProcessId, _isolatedDrainGrace.TotalSeconds);
                }
            }
            catch (OperationCanceledException)
            {
                // Only happens on a real user-cancel (ct). Surface whatever we have as empty.
                stdout = string.Empty;
                stderr = string.Empty;
            }

            sw.Stop();

            if (userCancel || timedOut)
            {
                return new PowerShellExecutionResult
                {
                    Success = false,
                    ExitCode = -1,
                    Output = stdout.TrimEnd(),
                    Error = userCancel
                        ? "Script execution cancelled"
                        : $"Script execution timed out after {request.Timeout!.Value.TotalSeconds:0}s",
                    TimedOut = timedOut,
                    Duration = sw.Elapsed,
                };
            }

            var exitCode = launched.GetExitCode();
            // Error-based success (see non-isolated path): fail only on a terminating error
            // (ErrorMarker), not on `exit N`.
            var success = !stdout.Contains(PowerShellScriptWrapper.ErrorMarker, StringComparison.Ordinal);
            var error = stderr.TrimEnd();

            // Heuristic-only attribution (no completion-port): if a cap was set and the script
            // actually FAILED (a terminating error — e.g. an OutOfMemory throw under a memory cap),
            // append a HEDGED hint — never claim it as fact, and never on a successful `exit N`.
            if (!success && request.IsolationLimits is { } lim
                && (lim.MemoryLimitMb is > 0 || lim.MaxProcesses is > 0))
            {
                const string hint = "Note: a process-isolation memory/process limit was active — "
                    + "the failure may have been caused by it (or the job tree was terminated by isolation).";
                error = string.IsNullOrEmpty(error) ? hint : error + Environment.NewLine + hint;
            }

            return new PowerShellExecutionResult
            {
                Success = success,
                ExitCode = exitCode,
                Output = stdout.TrimEnd(),
                Error = error,
                TimedOut = false,
                Duration = sw.Elapsed,
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new PowerShellExecutionResult
            {
                Success = false,
                ExitCode = -1,
                Error = $"Isolated execution failed: {ex.Message}",
                Duration = sw.Elapsed,
            };
        }
        finally
        {
            if (tempScript is not null)
            {
                try { File.Delete(tempScript); } catch { /* best-effort: temp script may already be gone */ }
            }
        }
    }

    /// <summary>
    /// Drains the isolated stdout/stderr reads with a bounded grace. Call ONLY after the root process
    /// has exited and its job tree has been terminated: at that point no legitimate writer to the pipe
    /// remains, so a read that has not reached EOF within <paramref name="grace"/> is blocked by a
    /// leaked inherited pipe handle in an unrelated process and is abandoned (and observed, so its
    /// eventual fault never raises an UnobservedTaskException). <c>DrainTimedOut</c> reports the leak.
    /// A real user cancel (<paramref name="ct"/>) propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    internal static async Task<(string Stdout, string Stderr, bool DrainTimedOut)> DrainReadsAsync(
        Task<string> stdoutTask, Task<string> stderrTask, TimeSpan grace, CancellationToken ct)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(grace, ct).ConfigureAwait(false);
            return (await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false), false);
        }
        catch (TimeoutException)
        {
            return (ObserveAbandonedRead(stdoutTask), ObserveAbandonedRead(stderrTask), true);
        }
    }

    /// <summary>
    /// Returns a completed read's text, or empty for a read still blocked by a leaked inherited pipe
    /// handle. A still-pending read is observed via a continuation so its eventual fault
    /// (ObjectDisposedException once the reader is disposed) never raises an UnobservedTaskException.
    /// </summary>
    private static string ObserveAbandonedRead(Task<string> readTask)
    {
        if (readTask.IsCompletedSuccessfully)
            return readTask.Result;
        _ = readTask.ContinueWith(static t => { _ = t.Exception; },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return string.Empty;
    }

    private static string? FindExecutable(string windowsName, string unixName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "where" : "which",
                Arguments = OperatingSystem.IsWindows() ? windowsName : unixName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            // Gate the redirected spawn too (see ProcessSpawnCoordinator) — this runs at engine
            // construction, but serializing it keeps the "every inheritable spawn is gated" invariant.
            Process? p;
            lock (ProcessSpawnCoordinator.Gate)
            {
                p = Process.Start(psi);
            }
            using var _p = p;
            var output = p?.StandardOutput.ReadToEnd().Trim();
            p?.WaitForExit(3000);
            if (p?.ExitCode == 0 && !string.IsNullOrEmpty(output))
                return output.Split('\n')[0].Trim();
        }
        catch
        {
            // best-effort discovery: missing 'where'/'which' or non-executable shell falls through to OS-default heuristic below.
        }

        if (OperatingSystem.IsWindows())
        {
            var knownPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
                @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            };
            foreach (var path in knownPaths)
                if (File.Exists(path) && Path.GetFileName(path).Equals(windowsName, StringComparison.OrdinalIgnoreCase))
                    return path;
        }

        return null;
    }

    private static async Task WritePrivateScriptAsync(string path, string content, CancellationToken ct)
    {
        FileStream? stream = null;
        var createdFile = false;
        try
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            createdFile = true;
            ApplyRestrictiveAcl(path);

            var bytes = Encoding.UTF8.GetBytes(content);
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }
        catch
        {
            if (createdFile)
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort: file may be locked or already gone */ }
            }
            throw;
        }
        finally
        {
            if (stream is not null)
            {
                try { await stream.DisposeAsync(); } catch { /* best-effort cleanup */ }
            }
        }
    }

    private static void ApplyRestrictiveAcl(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return;
        }

        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Could not determine current Windows identity for script ACL owner.");

        var fi = new FileInfo(path);
        var acl = fi.GetAccessControl();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var existing = acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in existing)
            acl.RemoveAccessRuleSpecific(rule);

        acl.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            AccessControlType.Allow));

        fi.SetAccessControl(acl);
    }
}
