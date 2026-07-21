using NodePilot.Core.Enums;

namespace NodePilot.Core.Models;

public class WorkflowExecution
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public ExecutionStatus Status { get; set; } = ExecutionStatus.Pending;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string? TriggeredBy { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// User who initiated the execution. Set by the controller on manual
    /// <c>POST /execute</c>; null for scheduler/trigger-driven runs. Used by the
    /// debug-resume endpoint to authorize step-through operations (only the
    /// starter may resume/step/stop a debug session).
    /// </summary>
    public Guid? StartedByUserId { get; set; }

    public string? TraceId { get; set; }
    public string? SpanId { get; set; }

    /// <summary>
    /// Parent execution that started this run via <c>startWorkflow</c>. Null for
    /// top-level runs (manual, scheduler, webhook, file/db/eventlog trigger).
    /// Set so post-mortem queries can reconstruct the call-chain without grepping
    /// the trace backend; the OTel span tree is still the primary tool, but DB
    /// reporting (UI execution-list, audit dashboards) needs a queryable link.
    /// </summary>
    public Guid? ParentExecutionId { get; set; }

    /// <summary>
    /// Depth in the sub-workflow call chain. 0 = top-level run, 1 = direct child of a
    /// <c>startWorkflow</c>, etc. Bounded by <c>WorkflowRecursion.MaxCallDepth</c>.
    /// Promoted out of the in-memory <c>__callDepth</c> input parameter so it survives
    /// process restarts and is queryable.
    /// </summary>
    public int CallDepth { get; set; }

    /// <summary>
    /// JSON object written by a <c>returnData</c> activity. Surfaced to callers
    /// via <c>startWorkflow</c> as OutputParameters, and to the API / UI as-is.
    /// Null if the workflow did not explicitly return data.
    /// </summary>
    public string? ReturnData { get; set; }

    /// <summary>
    /// JSON-serialized snapshot of the input parameters the execution was started with
    /// (<c>ExecuteWorkflowRequest.Parameters</c> for manual runs, trigger-injected params
    /// for scheduled/webhook/db/eventlog/file triggers, sub-workflow params for
    /// <c>startWorkflow</c> calls). Enables post-mortem reproduction of a run.
    /// Null if no parameters were provided.
    /// </summary>
    public string? InputParametersJson { get; set; }

    /// <summary>
    /// In active/passive HA mode: the cluster node id that owns this execution. Set on
    /// every Pending/Running insert (via the leader's <c>IClusterStateProvider.NodeId</c>).
    /// The failover-recovery sweep promotes any non-terminal row whose <c>OwnerNodeId</c>
    /// does not match the new leader to <c>Cancelled</c>, so a crashed leader's in-flight
    /// runs do not leak into "stuck Running forever".
    /// <para>
    /// Null on rows written before the cluster feature shipped — those are also recovered
    /// by the != ourNodeId predicate (NULL != "anything" is true in EF/SQL).
    /// </para>
    /// </summary>
    public string? OwnerNodeId { get; set; }

    /// <summary>
    /// For a <see cref="ExecutionStatus.Cancelled"/> execution: who/what initiated the cancel.
    /// One of "user" (a single manual <c>POST /executions/{id}/cancel</c>), "cancelAll"
    /// (workflow cancel-all / quarantine), "failover" (cluster leader-change recovery),
    /// "reconciler" (single-node startup recovery of a hung run), "dispatch" (terminal write
    /// from the pre-ownership dispatch path), or "system" (engine cancel without an explicit
    /// reason — e.g. execution timeout or host shutdown). Null for non-cancelled executions.
    /// Surfaced to alerting as the <c>cancelledBy</c> event field so a rule can target manual
    /// cancels only (<c>cancelledBy == "user"</c>).
    /// </summary>
    public string? CancelledBy { get; set; }

    public Workflow Workflow { get; set; } = null!;
    public ICollection<StepExecution> Steps { get; set; } = [];
}
