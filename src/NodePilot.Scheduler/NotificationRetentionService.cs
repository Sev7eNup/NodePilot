using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodePilot.Core.Enums;
using NodePilot.Data;
using NodePilot.Scheduler.Options;

namespace NodePilot.Scheduler;

/// <summary>
/// Trims the alerting delivery ledger so it doesn't grow unbounded. Simple Delete-Where, no
/// archive — delivery attempts are operational telemetry, not audit-grade. Leader-only.
///
/// <para>Deletes terminal (Sent/Failed) <c>NotificationDeliveryAttempt</c> rows older than the cutoff —
/// Pending rows are never touched (they are actively retried by the dispatcher). Also prunes stale
/// <c>NotificationSuppressionState</c> rows whose last fire is older than the cutoff. This is a no-op for
/// behaviour as long as a rule's cooldown window has expired by then — which the API enforces by capping
/// <c>CooldownMinutes</c>/<c>OccurrenceWindowMinutes</c> at 30 days, far below the default 90-day cutoff.
/// (If an operator lowers <c>Retention:Notifications:MaxAgeDays</c> below the 30-day throttle cap, keep it
/// above the longest configured cooldown to preserve that invariant.)</para>
///
/// <para>Config: <see cref="NotificationsRetentionOptions"/> (<c>Retention:Notifications:*</c> —
/// Enabled=true, MaxAgeDays=90, IntervalMinutes=360).</para>
/// </summary>
public class NotificationRetentionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    // Hot-reload: hold the live monitor (not a cached snapshot) so a config edit of
    // Retention:Notifications:* takes effect on the next sweep pass without a restart.
    private readonly IOptionsMonitor<RetentionOptions> _opts;
    private readonly NodePilot.Core.Interfaces.IClusterStateProvider _cluster;
    private readonly ILogger<NotificationRetentionService> _logger;

    // Resolved per pass from the live monitor — never cached across passes.
    private NotificationsRetentionOptions Opts => _opts.CurrentValue.Notifications;

    public NotificationRetentionService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<RetentionOptions> opts,
        NodePilot.Core.Interfaces.IClusterStateProvider cluster,
        ILogger<NotificationRetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _opts = opts;
        _cluster = cluster;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation("NotificationRetentionService started (hot-reload: per-pass config).");

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

            var interval = TimeSpan.FromMinutes(Math.Max(1, Opts.IntervalMinutes));
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("NotificationRetentionService stopped.");
    }

    /// <summary>
    /// Exactly one sweep iteration: reads the live config, skips when disabled, else runs one
    /// <see cref="PurgeOnceAsync"/> pass + heartbeat. No <c>Task.Delay</c> — the loop in
    /// <see cref="ExecuteAsync"/> owns the inter-pass spacing. Internal so unit tests can drive
    /// a single pass (incl. the hot-reload Enabled-toggle path) without the warm-up.
    /// </summary>
    internal async Task RunIterationAsync(CancellationToken ct)
    {
        // Hot-reload: a live toggle to Enabled=false parks the sweep instead of killing the
        // service, so flipping back to true later takes effect without a restart.
        var o = Opts;
        if (!o.Enabled)
        {
            _logger.LogDebug("NotificationRetentionService pass skipped (Retention:Notifications:Enabled=false).");
            return;
        }

        var maxAgeDays = Math.Max(1, o.MaxAgeDays);
        var intervalMinutes = Math.Max(1, o.IntervalMinutes);

        try
        {
            var sw = Stopwatch.StartNew();
            var deleted = await PurgeOnceAsync(maxAgeDays, ct);
            sw.Stop();
            if (deleted > 0)
                _logger.LogInformation("Pruned {Count} notification delivery/suppression rows older than {Days}d.", deleted, maxAgeDays);
            var tags = new TagList { new("nodepilot.retention.service", "notification_deliveries") };
            SchedulerMetrics.RetentionRowsDeleted.Add(deleted, tags);
            SchedulerMetrics.RetentionSweepDuration.Record(sw.Elapsed.TotalMilliseconds, tags);
            await HeartbeatAsync(intervalMinutes, $"ok: {deleted} pruned", ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var errTags = new TagList { new("nodepilot.retention.service", "notification_deliveries") };
            SchedulerMetrics.RetentionSweepErrors.Add(1, errTags);
            _logger.LogError(ex, "Notification retention sweep failed — retrying on next interval.");
        }
    }

    // Internal so unit tests can drive a single pass without the warm-up / interval.
    internal async Task<int> PurgeOnceAsync(int maxAgeDays, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();

        // Only terminal attempts — never delete a Pending row out from under the dispatcher's retry loop.
        var deletedAttempts = await db.NotificationDeliveryAttempts
            .Where(a => a.Status != NotificationDeliveryStatus.Pending && a.CreatedAt < cutoff)
            .ExecuteDeleteAsync(ct);

        var deletedSuppressions = await db.NotificationSuppressionStates
            .Where(s => s.LastFiredAt != null && s.LastFiredAt < cutoff)
            .ExecuteDeleteAsync(ct);

        // System-alert per-instance state (ADR 0008): prune rows whose instance hasn't been observed since the
        // cutoff — deleted credentials/workflows/completed executions leave state behind that would otherwise
        // accrete forever on an active policy. The evaluator also drops state for disabled/removed policies each
        // pass; this covers stale instances of still-active policies.
        var deletedPolicyStates = await db.SystemAlertPolicyStates
            .Where(s => s.LastObservedAt < cutoff)
            .ExecuteDeleteAsync(ct);

        return deletedAttempts + deletedSuppressions + deletedPolicyStates;
    }

    private async Task HeartbeatAsync(int intervalMinutes, string status, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        await SystemHealthWriter.BeatAsync(db, "NotificationRetentionService",
            expectedIntervalSeconds: intervalMinutes * 60, status: status, ct: ct);
    }
}
