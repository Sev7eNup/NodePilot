using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using NodePilot.Core.Interfaces;

namespace NodePilot.Remote;

public class WinRmSession : IRemoteSession
{
    private readonly Runspace _runspace;
    private readonly string? _targetHostname;
    // Set when a timeout or cancellation cut through ExecuteScriptAsync while it was still
    // running. PowerShell.Invoke() doesn't observe the cancellation token directly — Task.Run
    // only abandons the awaiter while the pipeline keeps running on the threadpool. Once that
    // happens the runspace is left in an unknown state and is unsafe to hand out to the next
    // pool consumer, so we mark the session poisoned and let the pool discard it on Return.
    private int _poisoned;

    public WinRmSession(Runspace runspace, string? targetHostname = null)
    {
        _runspace = runspace;
        _targetHostname = targetHostname;
    }

    /// <summary>
    /// True when the underlying runspace is still in the <c>Opened</c> state and we haven't
    /// flagged the session as poisoned by a prior timeout. Used by <see cref="WinRmSessionPool"/>
    /// to decide whether an idle pool entry can be handed out again or must be discarded.
    /// </summary>
    internal bool IsAlive => Volatile.Read(ref _poisoned) == 0
        && _runspace.RunspaceStateInfo.State == RunspaceState.Opened;

    /// <summary>
    /// Dispose the underlying runspace. Bypasses the pool — called by the pool itself when
    /// an idle entry ages out, or when the process is shutting down. Normal per-step
    /// disposal goes through <see cref="PooledWinRmSession"/>, which returns the session
    /// to the pool instead of closing it.
    /// </summary>
    internal ValueTask DisposeUnpooledAsync() => DisposeAsync();

    public async Task<RemoteExecutionResult> ExecuteScriptAsync(string script, int? timeoutSeconds = null, CancellationToken ct = default)
    {
        // PowerShell's module auto-loader is not thread-safe under high concurrency: when many
        // sessions simultaneously import the same module (e.g. NetTCPIP via Get-NetIPAddress)
        // the module registry's internal List<T> throws "Collection was modified; enumeration
        // operation may not execute." This is transient — a short random back-off and retry
        // reliably succeeds once the first importer has finished. Retry up to 2 more times.
        const int maxAttempts = 3;
        RemoteExecutionResult result = default!;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            result = await ExecuteOnceAsync(script, timeoutSeconds, ct);
            if (result.Success || attempt == maxAttempts) break;
            if (!IsModuleLoadRace(result.ErrorOutput)) break;
            await Task.Delay(Random.Shared.Next(100 * attempt, 350 * attempt), ct);
        }
        return result;
    }

    // Returns true when the error is the well-known PowerShell concurrent-module-load race.
    private static bool IsModuleLoadRace(string? error) =>
        error is not null && error.Contains("Collection was modified", StringComparison.OrdinalIgnoreCase);

    private async Task<RemoteExecutionResult> ExecuteOnceAsync(string script, int? timeoutSeconds, CancellationToken ct)
    {
        using var activity = WinRmSessionFactory.RemoteSource.StartActivity("winrm.execute", ActivityKind.Client);
        if (!string.IsNullOrEmpty(_targetHostname))
            activity?.SetTag("nodepilot.remote.target", _targetHostname);
        activity?.SetTag("nodepilot.remote.transport", "winrm");
        activity?.SetTag("nodepilot.remote.script.bytes", Encoding.UTF8.GetByteCount(script));
        activity?.SetTag("nodepilot.remote.timeout_sec", timeoutSeconds);

        var sw = Stopwatch.StartNew();
        var output = new StringBuilder();
        var errors = new StringBuilder();

        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript(script);

        // BeginInvoke runs the pipeline without occupying a ThreadPool worker for its duration.
        // Timeout and caller cancellation share a linked CTS that calls ps.Stop(). Because Stop
        // can leave the runspace undefined, the pool discards the poisoned session on return.
        //
        // timeoutSeconds null or <=0 means "no timeout" — only the parent cancellation token (ct)
        // can cancel the call.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutSeconds is { } secs && secs > 0)
            cts.CancelAfter(TimeSpan.FromSeconds(secs));

        IAsyncResult asyncResult;
        try
        {
            asyncResult = ps.BeginInvoke();
        }
        catch (Exception ex)
        {
            sw.Stop();
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            RemoteMetrics.ScriptDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("result", "fail"));
            return new RemoteExecutionResult
            {
                Success = false,
                ErrorOutput = ex.Message,
                Duration = sw.Elapsed
            };
        }

        using var ctRegistration = cts.Token.Register(() =>
        {
            try { ps.Stop(); } catch { /* best-effort: pipeline may already be torn down */ }
        });

        try
        {
            var results = await Task.Factory.FromAsync(asyncResult, ps.EndInvoke);

            foreach (var result in results)
                output.AppendLine(result?.ToString());

            foreach (var error in ps.Streams.Error)
                errors.AppendLine(error.ToString());

            sw.Stop();

            var stdout = output.ToString().TrimEnd();
            var stderr = errors.ToString().TrimEnd();
            activity?.SetTag("nodepilot.remote.stdout.bytes", Encoding.UTF8.GetByteCount(stdout));
            activity?.SetTag("nodepilot.remote.stderr.bytes", Encoding.UTF8.GetByteCount(stderr));

            var success = !ps.HadErrors;
            if (success)
                activity?.SetStatus(ActivityStatusCode.Ok);
            else
                activity?.SetStatus(ActivityStatusCode.Error, stderr);

            RemoteMetrics.ScriptDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("result", success ? "ok" : "fail"));

            return new RemoteExecutionResult
            {
                Success = success,
                Output = stdout,
                ErrorOutput = stderr,
                Duration = sw.Elapsed
            };
        }
        catch (PipelineStoppedException) when (cts.IsCancellationRequested)
        {
            Volatile.Write(ref _poisoned, 1);
            sw.Stop();
            var cancelled = ct.IsCancellationRequested;
            activity?.SetStatus(ActivityStatusCode.Error, cancelled ? "cancelled" : "timeout");
            activity?.SetTag("nodepilot.remote.timeout", true);
            RemoteMetrics.ScriptTimeouts.Add(1);
            RemoteMetrics.ScriptDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("result", cancelled ? "cancelled" : "timeout"));
            // A caller cancel is not a script failure - it is how a waitAny/waitNofM junction
            // stands down its losing branches, and StepRunner records those as Cancelled from the
            // exception. Returning Success=false marked them Failed, and one Failed step fails the
            // whole run. The session stays poisoned either way; only the verdict differs. A
            // timeout remains a failure.
            if (cancelled)
                throw new OperationCanceledException("Script execution cancelled", ct);
            return new RemoteExecutionResult
            {
                Success = false,
                ErrorOutput = $"Script execution timed out after {timeoutSeconds} seconds",
                Duration = sw.Elapsed
            };
        }

        catch (Exception ex)
        {
            sw.Stop();
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            RemoteMetrics.ScriptDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("result", "fail"));
            return new RemoteExecutionResult
            {
                Success = false,
                ErrorOutput = ex.Message,
                Duration = sw.Elapsed
            };
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_runspace.RunspaceStateInfo.State == RunspaceState.Opened)
            _runspace.Close();
        _runspace.Dispose();
        RemoteMetrics.SessionsActive.Add(-1);
        return ValueTask.CompletedTask;
    }
}
