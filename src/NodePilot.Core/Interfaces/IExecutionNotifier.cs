using NodePilot.Core.Enums;

namespace NodePilot.Core.Interfaces;

public interface IExecutionNotifier
{
    Task StepStartedAsync(Guid executionId, Guid workflowId, string stepId, string? stepName, string stepType, DateTime startedAt);
    /// <param name="outputVariable">
    /// Producing node's <c>data.outputVariable</c> alias (or null when not set). The live
    /// UI databus uses this to expose <c>{alias}.output</c> in addition to the
    /// <c>{stepId}.output</c> form, matching the engine's BuildStepVariables dual-lookup
    /// contract.
    /// </param>
    Task StepCompletedAsync(Guid executionId, Guid workflowId, string stepId, string? stepName, ExecutionStatus status, string? output, string? errorOutput, DateTime completedAt, IReadOnlyDictionary<string, string>? outputParameters = null, string? traceOutput = null, string? stepType = null, DateTime? startedAt = null, string? outputVariable = null);
    Task ExecutionStatusChangedAsync(Guid executionId, Guid workflowId, ExecutionStatus status, string? errorMessage, DateTime? completedAt);

    /// <summary>Signals that a step is paused at a breakpoint and waiting for resume.
    /// Variables have already passed through <c>OutputRedactor</c> — secrets are already
    /// masked, so it is safe to broadcast this to UI watchers.</summary>
    Task StepPausedAsync(Guid executionId, Guid workflowId, string stepId, string? stepName,
        IReadOnlyDictionary<string, string> variables, DateTime pausedAt, string reason);

    /// <summary>Signals that a paused step has been resumed. The frontend uses this to
    /// dismiss the debug-UI overlays.</summary>
    Task StepResumedAsync(Guid executionId, Guid workflowId, string stepId);
}
