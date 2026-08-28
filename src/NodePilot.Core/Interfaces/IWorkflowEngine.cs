using NodePilot.Core.Models;

namespace NodePilot.Core.Interfaces;

/// <summary>Debug resume command. Mirrors the <c>ResumeCommand</c> enum in the engine and is
/// redefined here so Core needs no dependency on the Engine project; the engine maps the two
/// onto each other one to one.</summary>
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
    /// Cancels a running execution by signalling its in-memory CancellationToken. Returns
    /// <c>true</c> when a matching token exists in this process, <c>false</c> when it does
    /// not, which happens for rows left behind by an earlier process instance. Callers should
    /// fall back to a direct DB status update on <c>false</c>. <paramref name="ct"/> applies
    /// only to the lookup, not to the cancelled execution, which uses its own token.
    /// <para><paramref name="cancelledBy"/> attributes the cancel (for example "user" for a
    /// manual single cancel) so the engine can record it on the execution row when it winds
    /// down to <c>Cancelled</c>; null is recorded as "system".</para>
    /// </summary>
    Task<bool> CancelAsync(Guid executionId, string? cancelledBy = null, CancellationToken ct = default);

    /// <summary>
    /// Resume command for an execution paused at a breakpoint. <paramref name="stepId"/>
    /// selects the paused node, so parallel branches can be resumed independently.
    /// <paramref name="overrides"/> are user-edited variable values merged into the variables
    /// before resuming. Returns false if the execution is not paused or the step is not waiting.
    /// </summary>
    bool Resume(Guid executionId, string stepId, DebugResumeCommand command,
        IReadOnlyDictionary<string, string>? overrides);

    /// <summary>Step ids of all currently paused steps of an execution. REST fallback for the
    /// frontend to rebuild the debug UI after a page reload that missed the SignalR event.
    /// </summary>
    IReadOnlyCollection<string> GetPausedSteps(Guid executionId);
}
