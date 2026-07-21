namespace NodePilot.Core.Interfaces;

/// <summary>
/// Process-wide back-pressure for sub-workflow invocations (<c>startWorkflow</c> +
/// <c>forEach</c>). Enforces a cap on simultaneously-running children so a fan-out
/// can't starve the engine of DB connections, runspaces, or thread-pool slots.
///
/// Default implementation is a single in-process semaphore — fine for single-instance
/// deployments. The interface exists so a future HA/multi-instance build can swap in
/// a distributed gate (DB lease, Redis, etc.) without touching the activity code.
/// </summary>
public interface ISubWorkflowGate
{
    /// <summary>
    /// Configured capacity (max concurrent children).
    /// </summary>
    int Capacity { get; }

    /// <summary>
    /// Currently-available slots. Used for tests and observability; do not gate
    /// admission decisions on the value (race-prone).
    /// </summary>
    int Available { get; }

    /// <summary>
    /// Acquires a slot, waiting up to <paramref name="timeout"/>. Returns
    /// <c>false</c> if the timeout elapses before a slot becomes available.
    /// Cancellation throws <see cref="System.OperationCanceledException"/>.
    /// </summary>
    Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Acquires a slot, waiting indefinitely. Cancellation throws
    /// <see cref="System.OperationCanceledException"/>.
    /// </summary>
    Task WaitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Releases one previously acquired slot. Caller is responsible for symmetry —
    /// every successful Wait must be paired with exactly one Release.
    /// </summary>
    void Release();
}
