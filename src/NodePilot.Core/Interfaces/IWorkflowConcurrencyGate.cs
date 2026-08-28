namespace NodePilot.Core.Interfaces;

/// <summary>
/// Per-workflow cap on concurrent executions (<c>Workflow.MaxConcurrentExecutions</c>). One
/// counter serves both admission paths so the limit holds across all callers: the durable
/// dispatcher, which cannot block and re-queues instead, and the synchronous sub-workflow
/// activities, which are already running and must wait.
/// <para>
/// The default implementation is in-process. That is exact rather than approximate because
/// every gated start happens on the cluster leader: the dispatch worker is leader-gated, and
/// the sub-workflow paths run inside an execution that is itself leader-owned. The interface
/// allows a distributed gate for a future active/active topology.
/// </para>
/// </summary>
public interface IWorkflowConcurrencyGate
{
    /// <summary>
    /// Takes a slot if one is free. Never waits — the dispatcher must return its worker so a
    /// refused run can stay queued in the outbox.
    /// </summary>
    /// <param name="observedLimit">
    /// The limit from the caller's freshly loaded workflow row. Seeds the gate's value unless
    /// a write path has pushed one via <see cref="SetLimit"/>.
    /// </param>
    bool TryAcquire(Guid workflowId, int? observedLimit);

    /// <summary>
    /// Takes a slot, waiting in FIFO order until one is free. For the synchronous sub-workflow
    /// paths, which have nowhere to be queued back to. Cancellation throws
    /// <see cref="OperationCanceledException"/> and removes the waiter.
    /// </summary>
    Task AcquireAsync(Guid workflowId, int? observedLimit, CancellationToken cancellationToken);

    /// <summary>
    /// Returns one acquired slot. Every successful acquire must be paired with exactly one
    /// release.
    /// </summary>
    void Release(Guid workflowId);

    /// <summary>
    /// Authoritative update from a write path, applied after its transaction commits. Wins over
    /// observed values for as long as the workflow has active runs, so a read that happened
    /// before the change cannot restore the old limit. A no-op when the workflow is idle: the
    /// next acquire seeds the fresh value anyway.
    /// </summary>
    void SetLimit(Guid workflowId, int? limit);

    /// <summary>
    /// Workflows currently at their limit. The dispatch claim query skips their outbox rows so
    /// a saturated workflow cannot fill every candidate slot and starve the others.
    /// Entries expire, so a limit raised without a <see cref="SetLimit"/> push still recovers.
    /// </summary>
    Guid[] BlockedWorkflowIds { get; }
}
