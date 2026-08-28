using NodePilot.Core.Interfaces;

namespace NodePilot.Engine.Activities;

/// <summary>
/// In-process default for <see cref="ISubWorkflowGate"/>. Backs the cap with a
/// <see cref="SemaphoreSlim"/>; lifetime is Singleton so all activities in the process share
/// the same pool. The cap limits concurrent startWorkflow calls; raising it does not help once
/// downstream resources (DB pool, runspace pool, CIM provider) saturate, so the default balances
/// keeping the queue moving against overloading those resources.
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
