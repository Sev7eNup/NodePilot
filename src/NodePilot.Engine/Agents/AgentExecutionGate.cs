using Microsoft.Extensions.Options;
using NodePilot.Core.Agents;

namespace NodePilot.Engine.Agents;

public sealed class AgentExecutionGate : IDisposable
{
    private readonly SemaphoreSlim _gate;
    public AgentExecutionGate(IOptions<AgentOptions> options)
    {
        var capacity = options.Value.MaxConcurrentRuns;
        if (capacity is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(options), "Agent concurrency must be between 1 and 32.");
        _gate = new SemaphoreSlim(capacity, capacity);
    }

    public async Task<Lease> AcquireAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        return new Lease(_gate);
    }

    public void Dispose() => _gate.Dispose();

    public sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private bool _held = true;
        private bool _disposed;
        public async Task<T> WhileReleasedAsync<T>(Func<Task<T>> operation, CancellationToken ct)
        {
            if (!_held || _disposed) throw new InvalidOperationException("Agent lease is not held.");
            _held = false;
            gate.Release();
            try { return await operation(); }
            finally
            {
                await gate.WaitAsync(ct);
                _held = true;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_held) { _held = false; gate.Release(); }
        }
    }
}
