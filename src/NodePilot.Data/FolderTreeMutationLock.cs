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

    /// <summary>Waits for the tree; dispose the result to release it.</summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        return new Lease(_gate);
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
