namespace NodePilot.Api.ExecutionDispatch;

internal enum ExecutionDispatchOutcome
{
    Completed,

    /// <summary>
    /// Ownership never transferred to the engine, so the durable intent is preserved and the
    /// item is retried shortly.
    /// </summary>
    RetryBeforeStart,

    /// <summary>
    /// The workflow was at its own concurrency limit. Kept separate from
    /// <see cref="RetryBeforeStart"/> so the two do not merge in the dispatch metric: this one
    /// is normal queueing, not a failed handoff, and it backs off further.
    /// </summary>
    DeferredByConcurrencyLimit,
}
