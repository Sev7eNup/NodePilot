using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using NodePilot.Core.Interfaces;

namespace NodePilot.Api.ExecutionDispatch;

public sealed class ExecutionDispatchQueue : IExecutionDispatchQueue
{
    private readonly ConcurrentQueue<Func<CancellationToken, Task>> _interactiveQueue = new();
    private readonly ConcurrentQueue<Func<CancellationToken, Task>> _normalQueue = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly SemaphoreSlim _capacity;

    public ExecutionDispatchQueue(IOptions<ExecutionDispatchOptions> options)
    {
        var capacity = Math.Max(1, options.Value.Capacity);
        _capacity = new SemaphoreSlim(capacity, capacity);
    }

    public ValueTask EnqueueAsync(
        Func<CancellationToken, Task> workItem,
        CancellationToken ct,
        ExecutionDispatchPriority priority = ExecutionDispatchPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        return EnqueueCoreAsync(workItem, ct, priority);
    }

    internal async ValueTask<Func<CancellationToken, Task>> DequeueAsync(CancellationToken ct)
    {
        await _available.WaitAsync(ct);
        if (_interactiveQueue.TryDequeue(out var interactiveWorkItem))
        {
            _capacity.Release();
            return interactiveWorkItem;
        }

        if (_normalQueue.TryDequeue(out var normalWorkItem))
        {
            _capacity.Release();
            return normalWorkItem;
        }

        _capacity.Release();
        throw new InvalidOperationException("Execution dispatch queue signalled work but no work item was available.");
    }

    private async ValueTask EnqueueCoreAsync(
        Func<CancellationToken, Task> workItem,
        CancellationToken ct,
        ExecutionDispatchPriority priority)
    {
        await _capacity.WaitAsync(ct);
        if (priority == ExecutionDispatchPriority.Interactive)
            _interactiveQueue.Enqueue(workItem);
        else
            _normalQueue.Enqueue(workItem);
        _available.Release();
    }
}
