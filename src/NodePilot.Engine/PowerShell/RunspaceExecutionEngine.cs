using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NodePilot.Engine.PowerShell;

/// <summary>
/// Executes PowerShell scripts in-process via RunspacePool.
/// Used for controlled system activities where performance matters.
/// Falls back gracefully if the SDK modules are incomplete.
/// </summary>
public sealed class RunspaceExecutionEngine : IPowerShellExecutionEngine, IDisposable
{
    private readonly ILogger _logger;
    private readonly int _minRunspaces;
    private readonly int _maxRunspaces;
    private readonly RunspacePool? _pool;
    private readonly bool _initFailed;
    private readonly string? _initError;

    public string EngineType => "runspace";
    public bool IsAvailable => !_initFailed;

    public RunspaceExecutionEngine(ILogger logger, int minRunspaces = 1, int maxRunspaces = 5)
    {
        _logger = logger;
        _minRunspaces = minRunspaces;
        _maxRunspaces = maxRunspaces;

        try
        {
            // Match the process-engine's `-ExecutionPolicy Bypass` flag so loading shipped
            // Windows modules (NetTCPIP / Get-NetIPAddress, Microsoft.PowerShell.Management)
            // doesn't fail on hosts with a Restricted/RemoteSigned default policy.
            // Without this, the in-process Runspace inherits the machine policy and refuses
            // to dot-source the .psm1 files those modules ship as.
            var iss = InitialSessionState.CreateDefault2();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            _pool = RunspaceFactory.CreateRunspacePool(iss);
            _pool.SetMinRunspaces(_minRunspaces);
            _pool.SetMaxRunspaces(_maxRunspaces);
            _pool.Open();
        }
        catch (Exception ex)
        {
            _initFailed = true;
            _initError = ex.Message;
            // Error, not Warning: falling back to the process engine costs 50-200 ms per
            // script (process spawn + module load) instead of <5 ms in the pool. On a fully
            // loaded system running 100 steps/s, that's a 5-20 s/s wall-clock deficit —
            // not a "silent slowdown" but a hard capacity problem. Operators need to see
            // this in the log, ideally with a pointer to the likely causes (missing
            // PowerShell SDK assemblies, .NET runtime mismatch, GPO-enforced execution policy).
            _logger.LogError(ex,
                "RunspacePool initialization failed — runScript falls back to per-script process spawn " +
                "(50-200 ms slower per script, ~30 MB RAM each). Likely causes: missing PowerShell SDK assemblies, " +
                ".NET runtime mismatch, or InitialSessionState policy refusal. Error: {Error}",
                ex.Message);
        }
    }

    public async Task<PowerShellExecutionResult> ExecuteAsync(PowerShellExecutionRequest request, CancellationToken ct)
    {
        // The in-process runspace pool shares the API host process and therefore cannot provide
        // the OS-enforced containment that `Isolated` promises. The factory never routes an
        // isolated request here (GetEngine(engineType, isolated) forces a process engine), so this
        // is a defensive self-documenting guard against a future caller wiring it up directly.
        if (request.Isolated)
        {
            return new PowerShellExecutionResult
            {
                Success = false,
                ExitCode = -1,
                Error = "The in-process runspace engine cannot honor process isolation; use a process engine (pwsh/powershell).",
            };
        }

        if (_initFailed || _pool is null)
        {
            return new PowerShellExecutionResult
            {
                Success = false,
                ExitCode = -1,
                Error = $"Runspace engine unavailable: {_initError}. Use 'auto', 'pwsh', or 'powershell' engine instead.",
            };
        }

        // PowerShell's module auto-loader is not thread-safe under high concurrency: when
        // many runspaces simultaneously import the same module (e.g. NetTCPIP via
        // Get-NetIPAddress) the module registry's internal List<T> throws "Collection was
        // modified; enumeration operation may not execute." Mirrors the retry pattern in
        // WinRmSession.ExecuteScriptAsync: short random back-off, up to 2 retries, only on
        // the well-known race signature (other failures pass through immediately).
        const int maxAttempts = 3;
        PowerShellExecutionResult result = default!;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            result = await ExecuteOnceAsync(request, ct);
            if (result.Success || attempt == maxAttempts) break;
            if (!IsModuleLoadRace(result.Error)) break;
            await Task.Delay(Random.Shared.Next(100 * attempt, 350 * attempt), ct);
        }
        return result;
    }

    internal static bool IsModuleLoadRace(string? error) =>
        error is not null && error.Contains("Collection was modified", StringComparison.OrdinalIgnoreCase);

    private async Task<PowerShellExecutionResult> ExecuteOnceAsync(PowerShellExecutionRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var output = new StringBuilder();
        var errors = new StringBuilder();
        var warnings = new StringBuilder();
        var verbose = new StringBuilder();

        using var ps = System.Management.Automation.PowerShell.Create();
        ps.RunspacePool = _pool;
        ps.AddScript(PowerShellScriptWrapper.Wrap(request.ScriptText, request.Parameters, _logger, request.OutputCaptureAllowlist));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (request.Timeout is { } configuredTimeout)
            cts.CancelAfter(configuredTimeout);

        // Real async via APM. BeginInvoke posts the script to PowerShell's internal
        // pipeline thread and returns an IAsyncResult immediately — unlike
        // Task.Run(() => ps.Invoke()) the awaiting Task does NOT park a ThreadPool
        // worker for the script duration. Cancellation/timeout is handled by ps.Stop()
        // via the CT registration, which makes EndInvoke throw PipelineStoppedException.
        IAsyncResult asyncResult;
        try
        {
            asyncResult = ps.BeginInvoke();
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new PowerShellExecutionResult
            {
                Success = false,
                ExitCode = -1,
                Error = ex.Message,
                Duration = sw.Elapsed,
            };
        }

        using var ctRegistration = cts.Token.Register(() =>
        {
            try { ps.Stop(); } catch { /* best-effort: pipeline may already be torn down */ }
        });

        try
        {
            var results = await Task.Factory.FromAsync(asyncResult, ps.EndInvoke);

            foreach (var r in results)
                output.AppendLine(SafeToString(r));
            foreach (var e in ps.Streams.Error)
                errors.AppendLine(SafeToString(e));
            foreach (var w in ps.Streams.Warning)
                warnings.AppendLine(SafeToString(w));
            foreach (var v in ps.Streams.Verbose)
                verbose.AppendLine(SafeToString(v));

            sw.Stop();
            // Non-terminating errors still indicate that the step did not complete cleanly.
            // Treating "any stdout" as success lets workflows continue after Write-Error.
            var success = !ps.HadErrors;
            return new PowerShellExecutionResult
            {
                Success = success,
                ExitCode = success ? 0 : 1,
                Output = output.ToString().TrimEnd(),
                Error = errors.ToString().TrimEnd(),
                Warning = warnings.ToString().TrimEnd(),
                Verbose = verbose.ToString().TrimEnd(),
                Duration = sw.Elapsed,
            };
        }
        catch (PipelineStoppedException) when (cts.IsCancellationRequested)
        {
            sw.Stop();
            // Distinguish caller-cancellation (parent ct) from our internal timeout firing.
            // Without an explicit timeout the only way we end up here is via parent ct.
            var isUserCancel = ct.IsCancellationRequested;
            return new PowerShellExecutionResult
            {
                Success = false,
                ExitCode = -1,
                TimedOut = !isUserCancel,
                Error = isUserCancel
                    ? "Script execution cancelled"
                    : $"Script timed out after {request.Timeout!.Value.TotalSeconds:0}s",
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
                Error = ex.Message,
                Duration = sw.Elapsed,
            };
        }
    }

    public void Dispose()
    {
        _pool?.Close();
        _pool?.Dispose();
    }

    // PSObject.ToString() can trigger the formatter (e.g. Format-Default) which itself needs a
    // runspace. When the pool runspace is mid-recycle or the object's formatter chain throws,
    // the bare ToString() call fails with "There is no Runspace available to run scripts in
    // this thread." Empirically seen for New-Service's ServiceController output. We unwrap to
    // BaseObject first (primitives, ServiceController.ServiceName etc. format safely) and fall
    // back to a typed placeholder if every ToString path throws — never let formatter errors
    // tear down step output.
    private static string SafeToString(object? value)
    {
        if (value is null) return string.Empty;
        if (value is PSObject ps)
        {
            var baseObj = ps.BaseObject;
            if (baseObj is string s) return s;
            try { return baseObj?.ToString() ?? string.Empty; } catch { /* fall through */ }
            try { return ps.ToString() ?? string.Empty; } catch { /* fall through */ }
            return $"<{baseObj?.GetType().Name ?? "PSObject"}: ToString failed>";
        }
        try { return value.ToString() ?? string.Empty; }
        catch { return $"<{value.GetType().Name}: ToString failed>"; }
    }
}
