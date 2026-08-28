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

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ExecutionDispatchSignal _signal;
    private readonly IClusterStateProvider _cluster;
    private readonly ILogger<ExecutionDispatchWorker> _logger;
    private readonly IDatabaseAvailability _availability;
    private readonly int _workerCount;

    public ExecutionDispatchWorker(
        IServiceScopeFactory scopeFactory,
        ExecutionDispatchSignal signal,
        IOptions<ExecutionDispatchOptions> options,
        IClusterStateProvider cluster,
        ILogger<ExecutionDispatchWorker> logger,
        IDatabaseAvailability availability)
    {
        _scopeFactory = scopeFactory;
        _signal = signal;
        _cluster = cluster;
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
                catch (Exception ex) when (DbErrorClassifier.Classify(ex) is not DbFailureKind.None)
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
                    if (outcome == ExecutionDispatchOutcome.RetryBeforeStart)
                    {
                        await ReleaseForRetryAsync(executionId.Value, leaseOwner, stoppingToken);
                        ApiMetrics.DispatchItemsProcessed.Add(1,
                            new KeyValuePair<string, object?>("result", "retry_before_start"));
                    }
                    else
                    {
                        ApiMetrics.DispatchItemsProcessed.Add(1,
                            new KeyValuePair<string, object?>("result", "success"));
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
                    await ReleaseForRetryAsync(executionId.Value, leaseOwner, CancellationToken.None);
                }

                _signal.Pulse();
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
        var candidates = await db.ExecutionDispatchOutbox.AsNoTracking()
            .Where(item => item.AvailableAt <= now
                           && (item.LeaseExpiresAt == null || item.LeaseExpiresAt <= now))
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

    private async Task ReleaseForRetryAsync(Guid executionId, string leaseOwner, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        await db.ExecutionDispatchOutbox
            .Where(item => item.ExecutionId == executionId && item.LeaseOwner == leaseOwner)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.LeaseOwner, (string?)null)
                .SetProperty(item => item.LeaseExpiresAt, (DateTime?)null)
                .SetProperty(item => item.AvailableAt, DateTime.UtcNow.AddSeconds(1)), ct);
    }
}
