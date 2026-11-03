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
    // Cap the persisted Variables snapshot size to avoid writing a huge dictionary to
    // the DB on every pause — the same DoS surface RedactAndCap closes for
    // InputParametersJson / ReturnData. Truncated snapshots get a marker so the
    // inspector UI can show they were cut.
    private const int MaxSnapshotChars = 64 * 1024;

    private readonly OutputRedactor _redactor;
    private readonly IExecutionNotifier _notifier;

    internal DebugCoordinator(OutputRedactor redactor, IExecutionNotifier notifier)
    {
        _redactor = redactor;
        _notifier = notifier;
    }

    /// <summary>
    /// Runs the full pause, await, and resume cycle for a single step. Mutates
    /// <paramref name="variables"/> in place (by-reference) when the resume command
    /// carries overrides, and toggles <c>debug.StepOverArmed</c> to propagate step-over
    /// to the next step. Throws <see cref="OperationCanceledException"/> if the max-pause
    /// guard fires or the execution gets cancelled while paused.
    /// </summary>
    /// <returns>
    /// The overrides the user applied, or null when they resumed without editing anything. The
    /// caller needs them because the activity config was already resolved before the pause: only
    /// the PowerShell-backed activities read the live variable dict at execute time, so every
    /// other activity has to have its config re-resolved against the edited values.
    /// </returns>
    internal async Task<IReadOnlyDictionary<string, string>?> HandlePauseAsync(
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
        var snapshotJson = OutputRedactor.Cap(JsonSerializer.Serialize(redactedVariables), MaxSnapshotChars);

        // Suspend the timeout — the execution clock should not keep running while
        // paused. It gets re-armed with the remaining budget after the await below.
        var pauseStart = DateTime.UtcNow;
        debug.BeginPause(executionCts);

        // Max-pause guard: a hard upper limit against zombie executions in case the
        // user closes the browser tab without clicking Resume. Linked to the execution's
        // cancellation token source, so an explicit /cancel also aborts the pause.
        using var pauseGuard = CancellationTokenSource.CreateLinkedTokenSource(ct);
        pauseGuard.CancelAfter(TimeSpan.FromMinutes(debug.MaxPauseMinutes));

        // Register before either REST persistence or SignalR can expose the pause.
        var reason = causedByStepOver ? "stepOver" : "breakpoint";
        var resumeTask = debug.AwaitResumeAsync(node.Id, pauseGuard.Token,
            new DebugPausedStepSnapshot(node.Id, node.Data.Label, pauseStart, reason, redactedVariables));
        EngineMetrics.DebugSessionsActive.Add(1);
        var outcome = "failed";
        ResumeRequest resume;
        try
        {
            stepExecution.Status = ExecutionStatus.Paused;
            stepExecution.PausedAt = pauseStart;
            stepExecution.VariablesSnapshot = snapshotJson;
            await stepDb.SaveChangesAsync(ct);
            await _notifier.StepPausedAsync(execution.Id, execution.WorkflowId, node.Id,
                node.Data.Label, redactedVariables, pauseStart,
                reason);
            resume = await resumeTask;
            outcome = resume.Command.ToString();
        }
        catch (OperationCanceledException)
        {
            outcome = "guard_or_cancelled";
            throw;
        }
        finally
        {
            // Persistence/notification can fail before we await the registered continuation.
            // Cancel and observe it so no phantom paused step or token registration survives.
            await pauseGuard.CancelAsync();
            try { await resumeTask; }
            catch (OperationCanceledException) { }
            var pausedDuration = DateTime.UtcNow - pauseStart;
            EngineMetrics.DebugSessionsActive.Add(-1);
            EngineMetrics.DebugPauseDuration.Record(pausedDuration.TotalMilliseconds,
                new KeyValuePair<string, object?>("outcome", outcome));

            debug.EndPause(execution.StartedAt, executionCts);
        }
        EngineMetrics.DebugResumeCommands.Add(1,
            new KeyValuePair<string, object?>("mode", resume.Command.ToString()));

        // Merge in overrides — the user edited variable values before clicking Resume.
        IReadOnlyDictionary<string, string>? appliedOverrides = null;
        if (resume.Overrides is { Count: > 0 })
        {
            foreach (var (k, v) in resume.Overrides)
                variables[k] = v;
            // Context.Variables points at this same dict (by reference), so the PowerShell-backed
            // activities (runScript, custom:*) see the edit when they resolve their own templates.
            // Every other activity consumes the JsonElement StepRunner resolved BEFORE this pause,
            // so StepRunner re-resolves it from the returned overrides.
            appliedOverrides = resume.Overrides;
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
        return appliedOverrides;
    }
}
