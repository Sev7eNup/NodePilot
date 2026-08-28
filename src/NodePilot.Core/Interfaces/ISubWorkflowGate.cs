namespace NodePilot.Core.Interfaces;

/// <summary>
/// Process-wide back-pressure for sub-workflow invocations (<c>startWorkflow</c> and
/// <c>forEach</c>). Caps how many children run at once so a fan-out cannot starve the engine
/// of DB connections, runspaces, or thread-pool slots. The default implementation is a single
/// in-process semaphore; the interface allows a distributed gate (DB lease, Redis) for
/// multi-instance deployments without changing the activity code.
/// </summary>
public interface ISubWorkflowGate
{
    /// <summary>
    /// Configured capacity (max concurrent children).
    /// </summary>
    int Capacity { get; }

    /// <summary>
    /// Number of free slots. For tests and observability only; the value races, so do not
    /// base admission decisions on it.
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
    /// Releases one previously acquired slot. Every successful Wait must be paired with
    /// exactly one Release.
    /// </summary>
    void Release();
}
