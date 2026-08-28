using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodePilot.Core.Enums;
using NodePilot.Data;
using NodePilot.Scheduler.Options;
using NodePilot.Data.Availability;

namespace NodePilot.Scheduler;

/// <summary>
/// Trims the alerting delivery ledger so it doesn't grow unbounded. Simple Delete-Where, no
/// archive — delivery attempts are operational telemetry, not audit-grade. Leader-only.
///
/// <para>Deletes terminal (Sent/Failed) <c>NotificationDeliveryAttempt</c> rows older than the
/// cutoff —
/// Pending rows are never touched (they are actively retried by the dispatcher). Also prunes stale
/// <c>NotificationSuppressionState</c> rows whose last fire is older than the cutoff. This is a
/// no-op for
/// behaviour as long as a rule's cooldown window has expired by then — which the API enforces by
/// capping
/// <c>CooldownMinutes</c>/<c>OccurrenceWindowMinutes</c> at 30 days, far below the default 90-day
/// cutoff.
/// (If an operator lowers <c>Retention:Notifications:MaxAgeDays</c> below the 30-day throttle cap,
/// keep it
/// above the longest configured cooldown to preserve that invariant.)</para>
///
/// <para>Config: <see cref="NotificationsRetentionOptions"/> (<c>Retention:Notifications:*</c> —
/// Enabled=true, MaxAgeDays=90, IntervalMinutes=360).</para>
/// </summary>
public class NotificationRetentionService : LeaderGatedRetentionService
{
    // Resolved per pass from the live monitor — never cached across passes.
    private NotificationsRetentionOptions Opts => _opts.CurrentValue.Notifications;

    public NotificationRetentionService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<RetentionOptions> opts,
        NodePilot.Core.Interfaces.IClusterStateProvider cluster,
        ILogger<NotificationRetentionService> logger,
        IDatabaseAvailability availability)
        : base(scopeFactory, opts, cluster, logger, availability)
    {
    }

    protected override string ServiceName => nameof(NotificationRetentionService);
    protected override string MetricServiceTag => "notification_deliveries";
    protected override TimeSpan WarmUpDelay => TimeSpan.FromSeconds(60);
    protected override int MinIntervalMinutes => 1;
    protected override int ConfiguredIntervalMinutes => Opts.IntervalMinutes;

    /// <summary>
    /// Exactly one sweep iteration: reads the live config, skips when disabled, else runs one
    /// <see cref="PurgeOnceAsync"/> pass + heartbeat. No <c>Task.Delay</c> — the loop in
    /// <see cref="ExecuteAsync"/> owns the inter-pass spacing. Internal so unit tests can drive
    /// a single pass (incl. the hot-reload Enabled-toggle path) without the warm-up.
    /// </summary>
    internal override async Task RunIterationAsync(CancellationToken ct)
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
            var tags = RetentionTags();
            SchedulerMetrics.RetentionRowsDeleted.Add(deleted, tags);
            SchedulerMetrics.RetentionSweepDuration.Record(sw.Elapsed.TotalMilliseconds, tags);
            await HeartbeatAsync(intervalMinutes, $"ok: {deleted} pruned", ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var errTags = RetentionTags();
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

        // Only terminal attempts — never delete a Pending row out from under the dispatcher's retry
        // loop.
        var deletedAttempts = await db.NotificationDeliveryAttempts
            .Where(a => a.Status != NotificationDeliveryStatus.Pending && a.CreatedAt < cutoff)
            .ExecuteDeleteAsync(ct);

        var deletedSuppressions = await db.NotificationSuppressionStates
            .Where(s => s.LastFiredAt != null && s.LastFiredAt < cutoff)
            .ExecuteDeleteAsync(ct);

        // System-alert per-instance state (ADR 0008): prune rows whose instance hasn't been
        // observed since the
        // cutoff — deleted credentials/workflows/completed executions leave state behind that would
        // otherwise
        // accrete forever on an active policy. The evaluator also drops state for disabled/removed
        // policies each
        // pass; this covers stale instances of still-active policies.
        var deletedPolicyStates = await db.SystemAlertPolicyStates
            .Where(s => s.LastObservedAt < cutoff)
            .ExecuteDeleteAsync(ct);

        return deletedAttempts + deletedSuppressions + deletedPolicyStates;
    }
}
