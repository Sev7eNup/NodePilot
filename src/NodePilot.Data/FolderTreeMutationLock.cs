namespace NodePilot.Data;

/// <summary>
/// Serialises structural changes to one folder tree within the process. Every mutation checks
/// the tree it read (cycle, depth, sibling names) and then writes; two concurrent moves that each
/// pass their check against the old tree can commit a cycle together, and no row constraint
/// stops that. Mutations are leader-only in a cluster, so a process-wide lock is sufficient.
/// </summary>
public sealed class FolderTreeMutationLock
{
    public static FolderTreeMutationLock SharedWorkflowFolders { get; } = new();
    public static FolderTreeMutationLock GlobalVariableFolders { get; } = new();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _epoch;

    /// <summary>
    /// Advances each time <see cref="BeginMutationAsync"/> takes the lock. A reader that captured
    /// it under the lock and still sees the same value later knows no mutation started meanwhile.
    /// </summary>
    public long Epoch => Interlocked.Read(ref _epoch);

    /// <summary>Waits for the tree; dispose the result to release it.</summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        return new Lease(_gate);
    }

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

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }
}
