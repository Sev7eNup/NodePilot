using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;

namespace NodePilot.Core.ExecutionDispatch;

public sealed record WorkflowDispatchSuppression(
    Guid WorkflowId,
    string TriggeredBy,
    string Reason);

public sealed record WorkflowDispatchIntent(
    Guid WorkflowId,
    string TriggeredBy,
    Dictionary<string, string>? Parameters,
    int? TimeoutSeconds = null,
    bool DebugEnabled = false,
    Guid? StartedByUserId = null,
    bool RequireWorkflowEnabled = false,
    string MissingWorkflowMessage = "Queued execution was not dispatched because the workflow no longer exists.",
    string PreOwnershipFailurePrefix = "Queued execution failed before the engine could take ownership",
    string EnqueueFailureMessage = "Queued execution was not dispatched because the request was cancelled before enqueue completed.",
    ExecutionStatus EnqueueFailureStatus = ExecutionStatus.Cancelled,
    ExecutionDispatchPriority Priority = ExecutionDispatchPriority.Normal,
    Func<WorkflowDispatchSuppression, CancellationToken, Task>? OnDispatchSuppressedAsync = null,
    // Maintenance-window admission control. Fresh fires (manual, trigger, webhook, external) leave
    // this true so the dispatch choke point re-checks the window even if it opened after the
    // caller's early check, closing the race. Recovery operations that re-run a known intent
    // (manual retry) set it false. Resume and sub-workflow calls bypass dispatch and never reach
    // this gate.
    bool RequireMaintenanceWindowCheck = true,
    // Set when an Admin force-runs through an active blackout (audited). Suppresses the gate.
    bool BypassMaintenanceWindow = false);

public interface IWorkflowExecutionDispatcher
{
    Task<WorkflowExecution> DispatchAsync(WorkflowDispatchIntent intent, CancellationToken ct);
}
