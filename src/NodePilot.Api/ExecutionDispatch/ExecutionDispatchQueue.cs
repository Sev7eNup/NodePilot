using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using NodePilot.Core.Interfaces;

namespace NodePilot.Api.ExecutionDispatch;

internal enum ExecutionDispatchOutcome
{
    Completed,
    RetryBeforeStart,
}

internal sealed record ExecutionDispatchWorkItem(
    Func<CancellationToken, Task<ExecutionDispatchOutcome>> ExecuteAsync,
    Func<CancellationToken, Task> LegacyCallback,
    ExecutionDispatchPriority Priority,
    bool HoldsCapacityPermit);

public sealed class ExecutionDispatchQueue : IExecutionDispatchQueue
{
    private readonly ConcurrentQueue<ExecutionDispatchWorkItem> _interactiveQueue = new();
    private readonly ConcurrentQueue<ExecutionDispatchWorkItem> _normalQueue = new();
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
        return EnqueueCoreAsync(
            new ExecutionDispatchWorkItem(
                async workerCt =>
                {
                    await workItem(workerCt);
                    return ExecutionDispatchOutcome.Completed;
                },
                workItem,
                priority,
                HoldsCapacityPermit: false),
            ct);
    }

    internal ValueTask EnqueueOutcomeAsync(
        Func<CancellationToken, Task<ExecutionDispatchOutcome>> workItem,
        CancellationToken ct,
        ExecutionDispatchPriority priority = ExecutionDispatchPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        return EnqueueCoreAsync(
            new ExecutionDispatchWorkItem(
                workItem,
                async workerCt => _ = await workItem(workerCt),
                priority,
                HoldsCapacityPermit: false),
            ct);
    }

    internal async ValueTask<Func<CancellationToken, Task>> DequeueAsync(CancellationToken ct)
        => (await DequeueWorkItemAsync(ct)).LegacyCallback;

    internal async ValueTask<ExecutionDispatchWorkItem> DequeueWorkItemAsync(CancellationToken ct)
    {
        await _available.WaitAsync(ct);
        if (_interactiveQueue.TryDequeue(out var interactiveWorkItem))
            return ReleaseDequeuedCapacity(interactiveWorkItem);

        if (_normalQueue.TryDequeue(out var normalWorkItem))
            return ReleaseDequeuedCapacity(normalWorkItem);

        throw new InvalidOperationException("Execution dispatch queue signalled work but no work item was available.");
    }

    internal ValueTask RequeueAsync(ExecutionDispatchWorkItem workItem, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        // Dequeue releases the queue-capacity permit before the work item runs. A concurrent
        // producer can claim that permit before the worker observes RetryBeforeStart. Waiting to
        // reacquire it would deadlock a single-worker queue: only that same worker can dequeue the
        // producer item that holds the permit. Reacquire opportunistically; otherwise the retry is
        // an uncounted overflow item (bounded by the worker count) and does not release a permit on
        // its next dequeue.
        _ = ct;
        var holdsCapacityPermit = _capacity.Wait(0);
        EnqueueQueuedItem(workItem with { HoldsCapacityPermit = holdsCapacityPermit });
        return ValueTask.CompletedTask;
    }

    private async ValueTask EnqueueCoreAsync(
        ExecutionDispatchWorkItem workItem,
        CancellationToken ct)
    {
        await _capacity.WaitAsync(ct);
        EnqueueQueuedItem(workItem with { HoldsCapacityPermit = true });
    }

    private void EnqueueQueuedItem(ExecutionDispatchWorkItem workItem)
    {
        if (workItem.Priority == ExecutionDispatchPriority.Interactive)
            _interactiveQueue.Enqueue(workItem);
        else
            _normalQueue.Enqueue(workItem);
        _available.Release();
    }

    private ExecutionDispatchWorkItem ReleaseDequeuedCapacity(ExecutionDispatchWorkItem workItem)
    {
        if (workItem.HoldsCapacityPermit)
            _capacity.Release();
        return workItem with { HoldsCapacityPermit = false };
    }
}
