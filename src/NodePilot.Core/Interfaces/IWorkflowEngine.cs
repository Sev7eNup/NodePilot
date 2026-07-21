using NodePilot.Core.Models;

namespace NodePilot.Core.Interfaces;

/// <summary>Debug resume command, mirroring the <c>ResumeCommand</c> enum in the engine.
/// Redefined here in the Core project so Core doesn't need a dependency on the Engine
/// project; the engine maps 1:1 between the two.</summary>
public enum DebugResumeCommand { Continue, StepOver, Stop }

public interface IWorkflowEngine
{
    Task<WorkflowExecution> ExecuteAsync(Workflow workflow, string triggeredBy, CancellationToken ct,
        Dictionary<string, string>? inputParameters = null,
        int? timeoutSeconds = null,
        bool debugEnabled = false,
        Guid? startedByUserId = null,
        Guid? parentExecutionId = null,
        int callDepth = 0,
        Guid? executionIdOverride = null,
        bool interactiveRun = false);

    /// <summary>
    /// Cancels a currently-running execution by signalling its in-memory CancellationToken.
    /// Returns <c>true</c> if the execution was found in this process and cancellation was
    /// signalled; <c>false</c> if no matching token exists (e.g. the execution was started
    /// by a previous process instance that has since restarted — a zombie row). Callers
    /// should fall back to a direct DB status update when <c>false</c> is returned.
    /// The <paramref name="ct"/> applies only to the lookup/signal operation itself (which
    /// is currently sync), not to the cancelled execution — that one always proceeds via
    /// its own token.
    /// <para><paramref name="cancelledBy"/> attributes the cancel (e.g. "user" for a manual
    /// single cancel) so the engine can record it on the execution row when it winds down to
    /// <c>Cancelled</c>; null falls back to "system" at the engine.</para>
    /// </summary>
    Task<bool> CancelAsync(Guid executionId, string? cancelledBy = null, CancellationToken ct = default);

    /// <summary>
    /// Resume command for an execution paused at a breakpoint. <paramref name="stepId"/>
    /// identifies the specific paused node (in parallel branches, each branch can be
    /// resumed independently). <paramref name="overrides"/> are user-edited variable
    /// values that get merged into the variables dict before resuming.
    /// Returns false if the execution isn't paused (or the step isn't waiting).
    /// </summary>
    bool Resume(Guid executionId, string stepId, DebugResumeCommand command,
        IReadOnlyDictionary<string, string>? overrides);

    /// <summary>Step ids of all currently paused steps of an execution. Used by the frontend
    /// after a page reload (when it missed the SignalR event) as a REST fallback to rebuild
    /// the debug UI.</summary>
    IReadOnlyCollection<string> GetPausedSteps(Guid executionId);
}
