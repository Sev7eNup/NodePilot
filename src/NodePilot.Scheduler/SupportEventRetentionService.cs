using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodePilot.Data;
using NodePilot.Scheduler.Options;

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
public class SupportEventRetentionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    // Hot-reload: hold the live monitor (not a cached snapshot) so a config edit of
    // Retention:SupportEvents:* takes effect on the next sweep pass without a restart.
    private readonly IOptionsMonitor<RetentionOptions> _opts;
    private readonly NodePilot.Core.Interfaces.IClusterStateProvider _cluster;
    private readonly ILogger<SupportEventRetentionService> _logger;

    // Resolved per pass from the live monitor — never cached across passes.
    private SupportEventsRetentionOptions Opts => _opts.CurrentValue.SupportEvents;

    public SupportEventRetentionService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<RetentionOptions> opts,
        NodePilot.Core.Interfaces.IClusterStateProvider cluster,
        ILogger<SupportEventRetentionService> logger)
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

        _logger.LogInformation("SupportEventRetentionService started (hot-reload: per-pass config).");

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

        _logger.LogInformation("SupportEventRetentionService stopped.");
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
            var tags = new TagList { new("nodepilot.retention.service", "support_events") };
            SchedulerMetrics.RetentionRowsDeleted.Add(deleted, tags);
            SchedulerMetrics.RetentionSweepDuration.Record(sw.Elapsed.TotalMilliseconds, tags);
            await HeartbeatAsync(intervalMinutes, $"ok: {deleted} pruned", ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var errTags = new TagList { new("nodepilot.retention.service", "support_events") };
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

    private async Task HeartbeatAsync(int intervalMinutes, string status, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        await SystemHealthWriter.BeatAsync(db, "SupportEventRetentionService",
            expectedIntervalSeconds: intervalMinutes * 60, status: status, ct: ct);
    }
}
