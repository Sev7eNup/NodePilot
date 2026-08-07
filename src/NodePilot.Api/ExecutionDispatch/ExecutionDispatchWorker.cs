using Microsoft.Extensions.Options;
using NodePilot.Api.Telemetry;

namespace NodePilot.Api.ExecutionDispatch;

public sealed class ExecutionDispatchWorker : BackgroundService
{
    private readonly ExecutionDispatchQueue _queue;
    private readonly NodePilot.Core.Interfaces.IClusterStateProvider _cluster;
    private readonly ILogger<ExecutionDispatchWorker> _logger;
    private readonly int _workerCount;

    private readonly NodePilot.Data.Availability.IDatabaseAvailability _availability;

    public ExecutionDispatchWorker(
        ExecutionDispatchQueue queue,
        IOptions<ExecutionDispatchOptions> options,
        NodePilot.Core.Interfaces.IClusterStateProvider cluster,
        ILogger<ExecutionDispatchWorker> logger,
        NodePilot.Data.Availability.IDatabaseAvailability availability)
    {
        _queue = queue;
        _cluster = cluster;
        _logger = logger;
        _availability = availability;
        _workerCount = Math.Max(1, options.Value.WorkerCount);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Starting execution dispatch worker pool with {WorkerCount} workers.",
            _workerCount);

        var workers = Enumerable.Range(0, _workerCount)
            .Select(index => RunWorkerAsync(index + 1, stoppingToken));
        return Task.WhenAll(workers);
    }

    private async Task RunWorkerAsync(int workerId, CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // HA gate: a follower MUST NOT pull work items off the dispatch queue.
                // If we did, an interactive /execute or webhook persisted by the active
                // leader (its row reads OwnerNodeId=leader) could be picked up here and
                // run twice. Dequeue is gated, not the queue itself — TryEnqueue still
                // works on followers (e.g. for HTTP requests that race the LB) but the
                // item just sits there until leadership flips.
                if (!_cluster.IsLeader)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                // Availability gate BEFORE the dequeue: a work item pulled while the database is gone
                // would burn its one chance against a dead server and leave its execution row stuck in
                // Pending until the next process restart. Parked items simply wait; TryEnqueue keeps
                // working (POST /execute is already 503-sealed by the middleware anyway).
                if (!await _availability.WaitUntilServableAsync(stoppingToken)) break;

                var workItem = await _queue.DequeueWorkItemAsync(stoppingToken);

                // Recovery race: the breaker can open between the gate above and this point (the
                // dequeue itself can suspend). Put the item back instead of running it — before it has
                // run, requeueing is trivially safe. Deliberately NOT done for an item that already
                // STARTED and then failed: its side effects are unknown, and double-starting an
                // execution is worse than a stuck-Pending row. The queue item retains its original
                // priority, so an interactive dispatch stays interactive across this race.
                if (!_availability.IsServable)
                {
                    await _queue.RequeueAsync(workItem, stoppingToken);
                    continue;
                }

                try
                {
                    var outcome = await workItem.ExecuteAsync(stoppingToken);
                    if (outcome == ExecutionDispatchOutcome.RetryBeforeStart)
                    {
                        await _queue.RequeueAsync(workItem, stoppingToken);
                        ApiMetrics.DispatchItemsProcessed.Add(1,
                            new KeyValuePair<string, object?>("result", "retry_before_start"));
                        continue;
                    }

                    // Explicitly typed KeyValuePair — a bare `new(...)` here is ambiguous
                    // between the `Counter<T>.Add(T, KVP)` and `Counter<T>.Add(T, params KVP[])` overloads.
                    ApiMetrics.DispatchItemsProcessed.Add(1,
                        new KeyValuePair<string, object?>("result", "success"));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    ApiMetrics.DispatchItemsProcessed.Add(1,
                        new KeyValuePair<string, object?>("result", "failure"));
                    _logger.LogError(
                        ex,
                        "Execution dispatch worker {WorkerId} failed processing queued work item.",
                        workerId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }
}
