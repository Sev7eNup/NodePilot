using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodePilot.Data;

namespace NodePilot.Scheduler;

/// <summary>
/// Trims expired external-trigger response keys and webhook replay claims out of
/// <c>IdempotencyKeys</c>. Unlike <see cref="ExecutionRetentionService"/>
/// and <see cref="AuditLogRetentionService"/>, this always runs and is deliberately not part of
/// <c>RetentionOptions</c> — the key-cache has no compliance value; keeping stale rows only grows
/// the per-request lookup cost on the hot external-trigger path. The cache is naturally capped by
/// the 24 h key TTL, so there is nothing for an operator to tune.
/// </summary>
public class IdempotencyKeyCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NodePilot.Core.Interfaces.IClusterStateProvider _cluster;
    private readonly ILogger<IdempotencyKeyCleanupService> _logger;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(30);

    public IdempotencyKeyCleanupService(
        IServiceScopeFactory scopeFactory,
        NodePilot.Core.Interfaces.IClusterStateProvider cluster,
        ILogger<IdempotencyKeyCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _cluster = cluster;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            // HA gate: leader-only.
            if (!_cluster.IsLeader)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                await RunIterationAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }

            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Exactly one sweep iteration (no Enabled gate — this sweep is always-on by design).
    /// No <c>Task.Delay</c> — the loop in <see cref="ExecuteAsync"/> owns the inter-pass
    /// spacing. Internal so unit tests can drive a single pass without the warm-up.
    /// </summary>
    internal async Task RunIterationAsync(CancellationToken ct)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var deleted = await PurgeOnceAsync(ct);
            sw.Stop();
            if (deleted > 0)
                _logger.LogDebug("Pruned {Count} expired idempotency keys.", deleted);
            var tags = new TagList { new("nodepilot.retention.service", "idempotency_key") };
            SchedulerMetrics.RetentionRowsDeleted.Add(deleted, tags);
            SchedulerMetrics.RetentionSweepDuration.Record(sw.Elapsed.TotalMilliseconds, tags);
            await HeartbeatAsync($"ok: {deleted} pruned", ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var errTags = new TagList { new("nodepilot.retention.service", "idempotency_key") };
            SchedulerMetrics.RetentionSweepErrors.Add(1, errTags);
            _logger.LogError(ex, "Idempotency-key sweep failed — retrying on next interval.");
        }
    }

    // Internal so unit tests can drive a single pass without the warm-up / interval.
    internal async Task<int> PurgeOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        var now = DateTime.UtcNow;
        return await db.IdempotencyKeys.Where(k => k.ExpiresAt < now).ExecuteDeleteAsync(ct);
    }

    private async Task HeartbeatAsync(string status, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        await SystemHealthWriter.BeatAsync(db, "IdempotencyKeyCleanupService",
            expectedIntervalSeconds: (int)SweepInterval.TotalSeconds, status: status, ct: ct);
    }
}
