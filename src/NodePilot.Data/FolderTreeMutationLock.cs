namespace NodePilot.Data;

/// <summary>
/// Serialises structural changes to one folder tree within the process. Every mutation checks
/// the tree it read (cycle, depth, sibling names) and then writes; two concurrent moves that each
/// pass their check against the old tree can commit a cycle together, and no row constraint
/// stops that. Mutations are leader-only in a cluster, so a process-wide lock is sufficient.
///
/// Readers that only need a tree no mutation is changing (hub joins, live-event fan-out,
/// dashboard scope) share the lock; mutations are exclusive. Waiters are served in arrival
/// order, so a queued mutation is not starved by a steady stream of readers.
/// </summary>
public sealed class FolderTreeMutationLock
{
    public static FolderTreeMutationLock SharedWorkflowFolders { get; } = new();
    public static FolderTreeMutationLock GlobalVariableFolders { get; } = new();

    private readonly object _sync = new();
    private readonly LinkedList<Waiter> _waiters = new();
    private int _readers;
    private bool _writer;
    private long _epoch;

    /// <summary>
    /// Advances each time <see cref="BeginMutationAsync"/> takes the lock. A reader that captured
    /// it under the lock and still sees the same value later knows no mutation started meanwhile.
    /// </summary>
    public long Epoch => Interlocked.Read(ref _epoch);

    /// <summary>Waits for exclusive access to the tree; dispose the result to release it.</summary>
    public Task<IDisposable> AcquireAsync(CancellationToken ct) => EnterAsync(exclusive: true, ct);

    /// <summary>
    /// Waits until no mutation is running, alongside other shared holders; dispose the result to
    /// release it. <see cref="Epoch"/> cannot change while the lease is held.
    /// </summary>
    public Task<IDisposable> AcquireSharedAsync(CancellationToken ct) => EnterAsync(exclusive: false, ct);

    /// <summary>
    /// Waits for the tree before a structural change and advances <see cref="Epoch"/> before the
    /// change begins; dispose the result to release it.
    /// </summary>
    public async Task<IDisposable> BeginMutationAsync(CancellationToken ct)
    {
        var lease = await AcquireAsync(ct);
        Interlocked.Increment(ref _epoch);
        return lease;
    }

    private Task<IDisposable> EnterAsync(bool exclusive, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<IDisposable>(ct);

        Waiter waiter;
        LinkedListNode<Waiter> node;
        lock (_sync)
        {
            if (_waiters.Count == 0 && CanGrant(exclusive))
            {
                Grant(exclusive);
                return Task.FromResult<IDisposable>(new Lease(this, exclusive));
            }

            waiter = new Waiter(exclusive);
            node = _waiters.AddLast(waiter);
        }

        if (ct.CanBeCanceled)
        {
            var registration = ct.Register(() => Cancel(node, waiter, ct));
            waiter.Completion.Task.ContinueWith(
                _ => registration.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        return waiter.Completion.Task;
    }

    private void Cancel(LinkedListNode<Waiter> node, Waiter waiter, CancellationToken ct)
    {
        lock (_sync)
        {
            // Not in the list any more means the lock was already handed to this waiter.
            if (node.List is null) return;
            _waiters.Remove(node);
            waiter.Completion.TrySetCanceled(ct);
            // The cancelled waiter may have been the one holding back those behind it.
            Pump();
        }
    }

    private bool CanGrant(bool exclusive) => exclusive ? !_writer && _readers == 0 : !_writer;

    private void Grant(bool exclusive)
    {
        if (exclusive) _writer = true;
        else _readers++;
    }

    private void Release(bool exclusive)
    {
        lock (_sync)
        {
            if (exclusive) _writer = false;
            else _readers--;
            Pump();
        }
    }

    // Hands the lock to waiters from the front of the queue for as long as the head can run.
    private void Pump()
    {
        while (_waiters.First is { } head && CanGrant(head.Value.Exclusive))
        {
            _waiters.RemoveFirst();
            Grant(head.Value.Exclusive);
            head.Value.Completion.SetResult(new Lease(this, head.Value.Exclusive));
        }
    }

    private sealed class Waiter(bool exclusive)
    {
        public bool Exclusive { get; } = exclusive;

        public TaskCompletionSource<IDisposable> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Lease(FolderTreeMutationLock owner, bool exclusive) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                owner.Release(exclusive);
        }
    }
}
