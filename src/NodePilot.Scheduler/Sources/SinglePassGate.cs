namespace NodePilot.Scheduler.Sources;

/// <summary>
/// Runs a pass for every request, but never two at once. A request that arrives while a pass is
/// running does not start a second one; it is folded into a single follow-up pass that begins after
/// the running one ends, so it still sees everything written up to the moment of the request.
/// </summary>
internal sealed class SinglePassGate
{
    private int _requested;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Requests a pass. Returns once this caller has nothing left to do: either its own pass ran, or
    /// another caller is running one and will cover the request.
    /// </summary>
    public async Task RunAsync(Func<Task> pass, CancellationToken ct)
    {
        Volatile.Write(ref _requested, 1);
        while (true)
        {
            if (!await _gate.WaitAsync(0, ct)) return;
            try
            {
                while (Interlocked.Exchange(ref _requested, 0) == 1)
                    await pass();
            }
            finally
            {
                _gate.Release();
            }
            // A request that came in after the last check but before the release has no runner.
            if (Volatile.Read(ref _requested) == 0) return;
        }
    }

    /// <summary>Blocks all passes until the returned lease is disposed; requests made meanwhile are kept.</summary>
    public async Task<IDisposable> HoldAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        return new Lease(_gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
