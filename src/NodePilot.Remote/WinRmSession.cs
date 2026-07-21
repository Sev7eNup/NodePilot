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
    // (Originally introduced under internal fix ticket F-1.)
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

        // Real timeout + cancellation handling, built on .NET's older Begin/End async pattern
        // (IAsyncResult) plus ps.Stop(). BeginInvoke posts the pipeline to the runspace's executor
        // thread and returns an IAsyncResult immediately, so the awaiting Task does NOT park a
        // ThreadPool worker for the script's duration (Task.Run(() => ps.Invoke()) used to). Both
        // timeout and caller cancellation flow through a linked CTS that triggers ps.Stop() on
        // fire — EndInvoke then throws PipelineStoppedException. The runspace may be left in an
        // undefined state after Stop, so we still poison the session and let the pool discard it
        // on Return.
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
            return new RemoteExecutionResult
            {
                Success = false,
                ErrorOutput = cancelled
                    ? "Script execution cancelled"
                    : $"Script execution timed out after {timeoutSeconds} seconds",
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
