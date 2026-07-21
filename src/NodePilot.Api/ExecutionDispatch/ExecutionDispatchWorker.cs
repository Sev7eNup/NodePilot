using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodePilot.Api.Telemetry;

namespace NodePilot.Api.ExecutionDispatch;

public sealed class ExecutionDispatchWorker : BackgroundService
{
    private readonly ExecutionDispatchQueue _queue;
    private readonly NodePilot.Core.Interfaces.IClusterStateProvider _cluster;
    private readonly ILogger<ExecutionDispatchWorker> _logger;
    private readonly int _workerCount;

    public ExecutionDispatchWorker(
        ExecutionDispatchQueue queue,
        IOptions<ExecutionDispatchOptions> options,
        NodePilot.Core.Interfaces.IClusterStateProvider cluster,
        ILogger<ExecutionDispatchWorker> logger)
    {
        _queue = queue;
        _cluster = cluster;
        _logger = logger;
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

                var workItem = await _queue.DequeueAsync(stoppingToken);
                try
                {
                    await workItem(stoppingToken);
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
