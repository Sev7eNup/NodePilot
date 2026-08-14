using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodePilot.Data;
using NodePilot.Scheduler.Options;
using NodePilot.Data.Availability;

namespace NodePilot.Scheduler;

/// <summary>
/// Trims old rows out of the <c>SupportEvents</c> table. Simple Delete-Where, no archive —
/// support events are not audit-grade; compliance retention lives in
/// <see cref="AuditLogRetentionService"/>. Leader-only.
///
/// <para>Config: <see cref="SupportEventsRetentionOptions"/> (<c>Retention:SupportEvents:*</c> —
/// Enabled=true, MaxAgeDays=90 matching the file-based support-log retention,
/// IntervalMinutes=360).</para>
/// </summary>
public class SupportEventRetentionService : LeaderGatedRetentionService
{
    // Resolved per pass from the live monitor — never cached across passes.
    private SupportEventsRetentionOptions Opts => _opts.CurrentValue.SupportEvents;

    public SupportEventRetentionService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<RetentionOptions> opts,
        NodePilot.Core.Interfaces.IClusterStateProvider cluster,
        ILogger<SupportEventRetentionService> logger,
        IDatabaseAvailability availability)
        : base(scopeFactory, opts, cluster, logger, availability)
    {
    }

    protected override string ServiceName => nameof(SupportEventRetentionService);
    protected override string MetricServiceTag => "support_events";
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
            _logger.LogDebug("SupportEventRetentionService pass skipped (Retention:SupportEvents:Enabled=false).");
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
                _logger.LogInformation("Pruned {Count} support events older than {Days}d.", deleted, maxAgeDays);
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
            _logger.LogError(ex, "Support-event retention sweep failed — retrying on next interval.");
        }
    }

    // Internal so unit tests can drive a single pass without the warm-up / interval.
    internal async Task<int> PurgeOnceAsync(int maxAgeDays, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        return await db.SupportEvents.Where(e => e.Timestamp < cutoff)
            .ExecuteDeleteAsync(ct);
    }
}
