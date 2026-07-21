using NodePilot.Core.Interfaces;

namespace NodePilot.Engine.Activities;

/// <summary>
/// In-process default for <see cref="ISubWorkflowGate"/>. Backs the cap with a
/// <see cref="SemaphoreSlim"/>; lifetime is Singleton so all activities in the
/// process share the same pool.
///
/// 64 was the long-standing default but proved fragile when 500-workflow stress
/// probes pushed 1500+ startWorkflow calls through it: the cap reads as serial
/// wait on the surface but lifting it to 600 actively *worsened* throughput
/// because the system saturates downstream (DB pool, runspace pool, CIM
/// provider). 128 lands in the "deep enough to keep the queue moving, shallow
/// enough not to thrash" band. (240 was tried 2026-05-07 with a 30 s WaitAsync
/// timeout and made wall time worse — longer WaitAsync just stretches saturation
/// rather than relieving it.)
/// </summary>
public sealed class InMemorySubWorkflowGate : ISubWorkflowGate, IDisposable
{
    public const int DefaultCapacity = 128;

    private readonly SemaphoreSlim _semaphore;

    public InMemorySubWorkflowGate() : this(DefaultCapacity) { }

    public InMemorySubWorkflowGate(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "capacity must be positive");
        Capacity = capacity;
        _semaphore = new SemaphoreSlim(capacity, capacity);
    }

    public int Capacity { get; }

    public int Available => _semaphore.CurrentCount;

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        => _semaphore.WaitAsync(timeout, cancellationToken);

    public Task WaitAsync(CancellationToken cancellationToken)
        => _semaphore.WaitAsync(cancellationToken);

    public void Release() => _semaphore.Release();

    public void Dispose() => _semaphore.Dispose();
}
