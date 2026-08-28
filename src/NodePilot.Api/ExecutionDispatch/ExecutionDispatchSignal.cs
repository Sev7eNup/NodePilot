using System.Threading.Channels;
using NodePilot.Core.ExecutionDispatch;

namespace NodePilot.Api.ExecutionDispatch;

public class ExecutionDispatchSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = false,
        SingleWriter = false,
    });

    public virtual void Pulse() => _channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan pollInterval, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(pollInterval);
        try
        {
            await _channel.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Periodic poll recovers durable rows whose process-local signal was lost.
        }
    }
}

public sealed class ExecutionDispatchCallbackRegistry
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid,
        Func<WorkflowDispatchSuppression, CancellationToken, Task>> _callbacks = new();

    public void Register(Guid executionId,
        Func<WorkflowDispatchSuppression, CancellationToken, Task>? callback)
    {
        if (callback is not null) _callbacks[executionId] = callback;
    }

    public bool TryGet(Guid executionId,
        out Func<WorkflowDispatchSuppression, CancellationToken, Task>? callback)
        => _callbacks.TryGetValue(executionId, out callback);

    public void Remove(Guid executionId) => _callbacks.TryRemove(executionId, out _);
}
