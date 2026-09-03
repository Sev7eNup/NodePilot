using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodePilot.Api.Telemetry;
using NodePilot.Core.Interfaces;
using NodePilot.Data;
using NodePilot.Data.Availability;

namespace NodePilot.Api.ExecutionDispatch;

public sealed class ExecutionDispatchWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Longer than <see cref="RetryDelay"/>: a workflow at its limit stays there until a run
    /// finishes, and the claim filter already skips it, so this only paces the residual race.
    /// </summary>
    private static readonly TimeSpan ConcurrencyDeferralDelay = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ExecutionDispatchSignal _signal;
    private readonly IClusterStateProvider _cluster;
    private readonly IWorkflowConcurrencyGate _concurrency;
    private readonly ILogger<ExecutionDispatchWorker> _logger;
    private readonly IDatabaseAvailability _availability;
    private readonly int _workerCount;

    public ExecutionDispatchWorker(
        IServiceScopeFactory scopeFactory,
        ExecutionDispatchSignal signal,
        IOptions<ExecutionDispatchOptions> options,
        IClusterStateProvider cluster,
        IWorkflowConcurrencyGate concurrency,
        ILogger<ExecutionDispatchWorker> logger,
        IDatabaseAvailability availability)
    {
        _scopeFactory = scopeFactory;
        _signal = signal;
        _cluster = cluster;
        _concurrency = concurrency;
        _logger = logger;
        _availability = availability;
        _workerCount = Math.Max(1, options.Value.WorkerCount);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Starting durable execution dispatch worker pool with {WorkerCount} workers.",
            _workerCount);

        return Task.WhenAll(Enumerable.Range(1, _workerCount)
            .Select(workerId => RunWorkerAsync(workerId, stoppingToken)));
    }

    private async Task RunWorkerAsync(int workerId, CancellationToken stoppingToken)
    {
        var leaseOwner = $"{_cluster.NodeId}:{workerId}:{Guid.NewGuid():N}";
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!_cluster.IsLeader)
                    {
                        await _signal.WaitAsync(PollInterval, stoppingToken);
                        continue;
                    }

                    if (!await _availability.WaitUntilServableAsync(stoppingToken)) break;

                    Guid? executionId;
                    try
                    {
                        executionId = await TryClaimAsync(leaseOwner, stoppingToken);
                    }
                    // Every claim failure backs off, not just the ones the classifier recognises as a
                    // database fault. A deadlock victim (Postgres 40P01 / SQL Server 1205) classifies
                    // as None by design, and many workers issuing overlapping claims against the same
                    // outbox rows is exactly the workload that produces one — the narrower filter
                    // re-threw it out of the worker.
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Durable dispatch worker {WorkerId} could not poll the outbox.", workerId);
                        await _signal.WaitAsync(PollInterval, stoppingToken);
                        continue;
                    }

                    if (executionId is null)
                    {
                        await _signal.WaitAsync(PollInterval, stoppingToken);
                        continue;
                    }

                    try
                    {
                        await using var scope = _scopeFactory.CreateAsyncScope();
                        var dispatcher = scope.ServiceProvider.GetRequiredService<ExecutionDispatchService>();
                        var outcome = await dispatcher.ProcessOutboxAsync(executionId.Value, stoppingToken);
                        // Every non-Completed outcome keeps its durable intent and must have its
                        // lease released, or the item sits idle for the full lease duration. Tags
                        // and back-off are per outcome so queueing and failed handoff stay apart.
                        switch (outcome)
                        {
                            case ExecutionDispatchOutcome.RetryBeforeStart:
                                await ReleaseForRetryAsync(
                                    executionId.Value, leaseOwner, RetryDelay, countAttempt: true, stoppingToken);
                                ApiMetrics.DispatchItemsProcessed.Add(1,
                                    new KeyValuePair<string, object?>("result", "retry_before_start"));
                                break;
                            case ExecutionDispatchOutcome.DeferredByConcurrencyLimit:
                                await ReleaseForRetryAsync(
                                    executionId.Value, leaseOwner, ConcurrencyDeferralDelay, countAttempt: false, stoppingToken);
                                ApiMetrics.DispatchItemsProcessed.Add(1,
                                    new KeyValuePair<string, object?>("result", "deferred_workflow_concurrency"));
                                break;
                            default:
                                ApiMetrics.DispatchItemsProcessed.Add(1,
                                    new KeyValuePair<string, object?>("result", "success"));
                                break;
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        ApiMetrics.DispatchItemsProcessed.Add(1,
                            new KeyValuePair<string, object?>("result", "failure"));
                        _logger.LogError(ex,
                            "Execution dispatch worker {WorkerId} failed processing outbox execution {ExecutionId}.",
                            workerId, executionId);
                        // The release is a second database write against the database that just
                        // failed. Unguarded it threw out of this catch block, past the loop, and
                        // faulted the worker task. The item keeps its durable intent, so losing the
                        // release only delays the retry until the lease expires.
                        try
                        {
                            await ReleaseForRetryAsync(
                                executionId.Value, leaseOwner, RetryDelay, countAttempt: true, CancellationToken.None);
                        }
                        catch (Exception releaseEx)
                        {
                            _logger.LogWarning(releaseEx,
                                "Execution dispatch worker {WorkerId} could not release the lease for {ExecutionId}; it will be retried when the lease expires.",
                                workerId, executionId);
                        }
                    }

                    _signal.Pulse();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Last-resort net. HostOptions.BackgroundServiceExceptionBehavior is deliberately
                    // left at StopHost, so anything escaping this loop takes the whole API process
                    // down — the opposite of the shed-load-and-recover contract in ADR 0011. Log,
                    // back off, keep the pool alive.
                    _logger.LogError(ex,
                          "Durable dispatch worker {WorkerId} hit an unexpected error; the worker continues.",
                          workerId);
                    ApiMetrics.DispatchItemsProcessed.Add(1,
                          new KeyValuePair<string, object?>("result", "worker_error"));
                  await _signal.WaitAsync(PollInterval, CancellationToken.None);
              }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    private async Task<Guid?> TryClaimAsync(string leaseOwner, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        var now = DateTime.UtcNow;
        var query = db.ExecutionDispatchOutbox.AsNoTracking()
            .Where(item => item.AvailableAt <= now
                           && (item.LeaseExpiresAt == null || item.LeaseExpiresAt <= now));

        // Skip workflows already at their concurrency limit. Without this, one saturated
        // workflow's queued rows fill every candidate slot (they are the oldest) and no other
        // workflow is ever seen. Only applied when the set is non-empty so the common case
        // keeps the exact SQL shape it has today.
        var blocked = _concurrency.BlockedWorkflowIds;
        if (blocked.Length > 0)
            query = query.Where(item => !blocked.Contains(item.WorkflowId));

        var candidates = await query
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.CreatedAt)
            .Select(item => item.ExecutionId)
            .Take(Math.Max(4, _workerCount))
            .ToListAsync(ct);

        foreach (var executionId in candidates)
        {
            var claimed = await db.ExecutionDispatchOutbox
                .Where(item => item.ExecutionId == executionId
                               && item.AvailableAt <= now
                               && (item.LeaseExpiresAt == null || item.LeaseExpiresAt <= now))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.LeaseOwner, leaseOwner)
                    .SetProperty(item => item.LeaseExpiresAt, now.Add(LeaseDuration))
                    .SetProperty(item => item.AttemptCount, item => item.AttemptCount + 1), ct);
            if (claimed == 1) return executionId;
        }

        return null;
    }

    /// <param name="countAttempt">
    /// False for a concurrency deferral: the claim already incremented the counter, and a run
    /// that waits an hour for a slot would otherwise show hundreds of attempts as if it kept
    /// failing. Rolling it back here keeps the number a handoff-failure signal.
    /// </param>
    private async Task ReleaseForRetryAsync(
        Guid executionId, string leaseOwner, TimeSpan delay, bool countAttempt, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        var claimed = db.ExecutionDispatchOutbox
            .Where(item => item.ExecutionId == executionId && item.LeaseOwner == leaseOwner);
        var availableAt = DateTime.UtcNow.Add(delay);

        if (countAttempt)
        {
            await claimed.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.LeaseOwner, (string?)null)
                .SetProperty(item => item.LeaseExpiresAt, (DateTime?)null)
                .SetProperty(item => item.AvailableAt, availableAt), ct);
            return;
        }

        await claimed.ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.LeaseOwner, (string?)null)
            .SetProperty(item => item.LeaseExpiresAt, (DateTime?)null)
            .SetProperty(item => item.AvailableAt, availableAt)
            .SetProperty(item => item.AttemptCount, item => item.AttemptCount > 0 ? item.AttemptCount - 1 : 0), ct);
    }
}
