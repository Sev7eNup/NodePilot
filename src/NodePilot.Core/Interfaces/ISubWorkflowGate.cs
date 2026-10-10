namespace NodePilot.Core.Interfaces;

/// <summary>
/// Process-wide back-pressure for sub-workflow invocations (<c>startWorkflow</c> and
/// <c>forEach</c>). Caps child workflows with active work; fully suspended ancestors lend their
/// slot until resumption (ADR 0018). The engine's separate execution cap also bounds suspended
/// ancestors. The default implementation is a single in-process semaphore.
/// </summary>
public interface ISubWorkflowGate
{
    /// <summary>
    /// Configured capacity (max child workflows with active work).
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
    /// Releases one acquired slot. Every successful Wait must be paired with
    /// exactly one Release.
    /// </summary>
    void Release();
}
