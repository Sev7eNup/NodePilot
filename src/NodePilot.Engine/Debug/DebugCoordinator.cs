using System.Text.Json;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Debug;

/// <summary>
/// Coordinates a single debug-pause lifecycle: redact+persist variables snapshot,
/// notify SignalR, suspend the execution timeout, await the user's resume command,
/// validate any override edits, re-arm the timeout with remaining budget, and write
/// the row back to Running. Extracted from WorkflowEngine so the concern is testable
/// and the engine stays focused on orchestration.
/// </summary>
internal sealed class DebugCoordinator
{
    // Bound the persisted Variables snapshot. Without a cap, a step that built a 50 MB
    // accumulator dict would write the whole thing into a NVARCHAR(MAX) on every pause
    // — same DoS surface RedactAndCap closes for InputParametersJson / ReturnData.
    // 64 KiB is enough for ~600 typical key/value pairs after redaction; truncated
    // snapshots get a marker so the inspector UI can show "this was truncated".
    private const int MaxSnapshotChars = 64 * 1024;

    private readonly OutputRedactor _redactor;
    private readonly IExecutionNotifier _notifier;

    internal DebugCoordinator(OutputRedactor redactor, IExecutionNotifier notifier)
    {
        _redactor = redactor;
        _notifier = notifier;
    }

    /// <summary>
    /// Runs the full pause → await → resume cycle for a single step. Mutates
    /// <paramref name="variables"/> in place (by-reference) when the resume command
    /// carries overrides, and toggles <c>debug.StepOverArmed</c> to propagate step-over
    /// to the next step. Throws <see cref="OperationCanceledException"/> if the max-pause
    /// guard fires or the execution gets cancelled while paused.
    /// </summary>
    internal async Task HandlePauseAsync(
        WorkflowExecution execution, WorkflowNode node, StepExecution stepExecution,
        NodePilotDbContext stepDb, Dictionary<string, string> variables,
        DebugHandle debug, CancellationTokenSource executionCts, CancellationToken ct)
    {
        // Consume the step-over flag as soon as the triggering step starts to pause.
        // Otherwise every subsequent step in parallel branches would pause again too.
        var causedByStepOver = debug.StepOverArmed;
        debug.StepOverArmed = false;

        // Redact the variables (secrets), then persist them as a JSON snapshot on the row.
        var redactedVariables = new Dictionary<string, string>(variables.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in variables)
            redactedVariables[k] = _redactor.RedactNamedValue(k, v) ?? v;
        var snapshotJson = JsonSerializer.Serialize(redactedVariables);
        if (snapshotJson.Length > MaxSnapshotChars)
            snapshotJson = snapshotJson.Substring(0, MaxSnapshotChars) + "... [truncated]";

        stepExecution.Status = ExecutionStatus.Paused;
        stepExecution.PausedAt = DateTime.UtcNow;
        stepExecution.VariablesSnapshot = snapshotJson;
        EngineMetrics.DebugSessionsActive.Add(1);
        // Eager write — the user needs to see the Paused row immediately when polling via
        // REST, even if _deferRunningStateWrite=true. Debugging is by nature "low-volume,
        // high-visibility", so the extra write is worth it.
        await stepDb.SaveChangesAsync(ct);

        await _notifier.StepPausedAsync(execution.Id, execution.WorkflowId, node.Id,
            node.Data.Label, redactedVariables, stepExecution.PausedAt.Value,
            causedByStepOver ? "stepOver" : "breakpoint");

        // Suspend the timeout — the execution clock should not keep running while
        // paused. It gets re-armed with the remaining budget after the await below.
        var pauseStart = DateTime.UtcNow;
        if (debug.OriginalTimeoutSeconds is > 0)
            executionCts.CancelAfter(Timeout.InfiniteTimeSpan);

        // Max-pause guard: a hard upper limit against zombie executions in case the
        // user closes the browser tab without clicking Resume. Linked to the execution's
        // cancellation token source, so an explicit /cancel also aborts the pause.
        using var pauseGuard = CancellationTokenSource.CreateLinkedTokenSource(ct);
        pauseGuard.CancelAfter(TimeSpan.FromMinutes(debug.MaxPauseMinutes));

        ResumeRequest resume;
        try
        {
            resume = await debug.AwaitResumeAsync(node.Id, pauseGuard.Token);
        }
        catch (OperationCanceledException)
        {
            // Pause guard expired, or the execution was cancelled → terminate as Cancelled.
            // The Paused row stays in the DB as a trace; the exception handler further up
            // the call stack rewrites the status to Failed.
            EngineMetrics.DebugSessionsActive.Add(-1);
            EngineMetrics.DebugPauseDuration.Record((DateTime.UtcNow - pauseStart).TotalMilliseconds,
                new KeyValuePair<string, object?>("outcome", "guard_or_cancelled"));
            throw;
        }

        var pausedDuration = DateTime.UtcNow - pauseStart;
        debug.TotalPausedDuration += pausedDuration;
        EngineMetrics.DebugSessionsActive.Add(-1);
        EngineMetrics.DebugPauseDuration.Record(pausedDuration.TotalMilliseconds,
            new KeyValuePair<string, object?>("outcome", resume.Command.ToString()));
        EngineMetrics.DebugResumeCommands.Add(1,
            new KeyValuePair<string, object?>("mode", resume.Command.ToString()));

        // Re-arm the timeout — remaining budget = originalTimeout - elapsed + totalPaused.
        if (debug.OriginalTimeoutSeconds is int origTimeout && origTimeout > 0)
        {
            var elapsed = DateTime.UtcNow - execution.StartedAt;
            var remaining = TimeSpan.FromSeconds(origTimeout) - elapsed + debug.TotalPausedDuration;
            if (remaining <= TimeSpan.Zero)
                await executionCts.CancelAsync();
            else
                executionCts.CancelAfter(remaining);
        }

        // Merge in overrides — the user edited variable values before clicking Resume.
        if (resume.Overrides is { Count: > 0 })
        {
            // Reject attempts to poison globals (read-only by design) or reserved engine
            // keys (e.g. __callDepth for sub-workflow recursion) — a security-audit
            // finding (C-2-b). Also cap the override size (finding L-2) to avoid a debug
            // client blasting megabytes of overrides into the variable dict.
            const int MaxOverrideEntries = 256;
            const int MaxOverrideValueBytes = 64 * 1024;
            if (resume.Overrides.Count > MaxOverrideEntries)
                throw new InvalidOperationException(
                    $"Debug override: too many entries ({resume.Overrides.Count}); max is {MaxOverrideEntries}.");
            foreach (var (k, v) in resume.Overrides)
            {
                if (string.IsNullOrEmpty(k))
                    throw new InvalidOperationException("Debug override: empty key is not allowed.");
                if (k.StartsWith("globals.", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Debug override: 'globals.*' keys are read-only during debug (offending key: '{k}').");
                if (k.StartsWith("__", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Debug override: reserved engine key '{k}' cannot be overridden.");
                if (v is not null && v.Length > MaxOverrideValueBytes)
                    throw new InvalidOperationException(
                        $"Debug override: value for '{k}' exceeds {MaxOverrideValueBytes} bytes.");
            }
            foreach (var (k, v) in resume.Overrides)
                variables[k] = v;
            // Context.Variables points at this same dict (by reference) → overrides are
            // immediately visible to the executor too, no rebuild needed.
        }

        if (resume.Command == ResumeCommand.Stop)
        {
            // Stop arrives as an explicit command rather than a cancellation-token cancel,
            // so the downstream Skipped-propagation behaves the same as a regular cancel.
            await executionCts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        }
        if (resume.Command == ResumeCommand.StepOver)
            debug.StepOverArmed = true;

        // Back to Running; clear PausedAt + the snapshot so the final row isn't cluttered
        // with debug metadata (the pause info only matters for the live session).
        stepExecution.Status = ExecutionStatus.Running;
        stepExecution.PausedAt = null;
        stepExecution.VariablesSnapshot = null;
        await stepDb.SaveChangesAsync(ct);

        await _notifier.StepResumedAsync(execution.Id, execution.WorkflowId, node.Id);
    }
}
