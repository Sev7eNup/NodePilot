using System.Collections.Concurrent;

namespace NodePilot.Engine.Debug;

/// <summary>
/// The resume command the user sends from the UI once an execution is paused at a
/// breakpoint. Continue = keep running until the next breakpoint. StepOver = run the
/// next step and then pause again immediately afterward (whether or not that step has
/// a breakpoint of its own). Stop = cancel the execution.
/// </summary>
public enum ResumeCommand { Continue, StepOver, Stop }

/// <summary>
/// Payload of the resume message. <c>Overrides</c> holds user-edited variable values —
/// this is the "what-if" part of the feature's UX: the user can change `{{globals.ENV}}`
/// from "dev" to "prod", click Continue, and the downstream steps see the new value.
/// The key format mirrors the variables dict: "globals.ENV", "manual.foo",
/// "stepName.param.host".
/// </summary>
public sealed record ResumeRequest(
    ResumeCommand Command,
    IReadOnlyDictionary<string, string>? Overrides);

/// <summary>
/// Per-execution state of the debugger. Lives only in the in-memory dictionary
/// <c>WorkflowEngine._debugHandles</c> — a process restart terminates any paused
/// executions. The engine calls <see cref="AwaitResumeAsync"/> when a breakpoint is
/// reached; the API controller calls <see cref="Resume"/> when the user clicks resume.
/// <see cref="_pending"/> is keyed by step ID so concurrent pauses on parallel
/// branches each get their own TaskCompletionSource.
/// </summary>
public sealed class DebugHandle
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ResumeRequest>> _pending = new();

    /// <summary>Maximum minutes a step is allowed to wait at a breakpoint before it is
    /// automatically cancelled. Guards against zombie executions when the user closes
    /// the browser tab. Config key: <c>Engine:Debug:MaxPauseMinutes</c>, default 10.</summary>
    public int MaxPauseMinutes { get; init; } = 10;

    /// <summary>Original execution timeout from the caller, in seconds. 0/null = no
    /// timeout. On resume we compute: remaining = OriginalTimeoutSeconds - elapsed +
    /// TotalPausedDuration, so time spent paused is not charged against the workflow's
    /// timeout budget.</summary>
    public int? OriginalTimeoutSeconds { get; init; }

    /// <summary>Next-stop flag: true when the last resume was a StepOver, meaning the
    /// next step must pause too, even without a breakpoint of its own.
    ///
    /// A single flag (not a per-step map) is enough because StepOver only applies to the
    /// very next step; consuming it resets it. With parallel branches, the first branch
    /// to pause consumes the flag and the rest keep running unhindered — a deliberate
    /// trade-off, since step-over is inherently ambiguous across parallel branches.</summary>
    public bool StepOverArmed { get; set; }

    /// <summary>Sum of all pause durations for this execution. Excludes debugger wait time
    /// from the workflow timeout budget.</summary>
    public TimeSpan TotalPausedDuration { get; set; }

    /// <summary>Waits for a resume command to arrive for the given step. The engine
    /// thread blocks here until either <see cref="Resume"/> is called or
    /// <paramref name="ct"/> is cancelled. The TaskCompletionSource is only registered
    /// inside this call, so a resume that arrives too early can't set a signal that
    /// nothing is listening for.</summary>
    public Task<ResumeRequest> AwaitResumeAsync(string stepId, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ResumeRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[stepId] = tcs;
        // If the outer token source is cancelled (e.g. the user hits /cancel while paused),
        // resolve the TaskCompletionSource with cancellation. Otherwise the engine's
        // awaiting task would hang until the pause guard's own timeout eventually fires.
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    /// <summary>Signals resume for the given step. Returns false if no step with this ID
    /// is currently paused and waiting (the user clicked resume on a step that isn't
    /// paused — the API responds 409).</summary>
    public bool Resume(string stepId, ResumeRequest request)
    {
        if (!_pending.TryRemove(stepId, out var tcs)) return false;
        return tcs.TrySetResult(request);
    }

    /// <summary>IDs of steps currently paused and waiting. For debugging / tests.</summary>
    public IEnumerable<string> PendingSteps => _pending.Keys;

    /// <summary>Signals "Stop" to every currently paused step, used by the cancel
    /// endpoint so the engine thread exits its pause cleanly and the execution
    /// terminates as Cancelled. Without this, cancelling the token source alone would
    /// leave the engine thread blocked until the pause guard's own timeout fires.</summary>
    public void ReleaseAllAsStop()
    {
        foreach (var key in _pending.Keys.ToList())
        {
            if (_pending.TryRemove(key, out var tcs))
                tcs.TrySetResult(new ResumeRequest(ResumeCommand.Stop, null));
        }
    }
}
