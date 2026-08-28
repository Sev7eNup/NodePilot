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
/// Trims old rows out of <c>TriggerDeliveryReceipts</c>. A receipt is written for every observed
/// trigger signal, so on a busy installation this table grows faster than any other the retention
/// services cover — and it had no sweeper at all until now.
///
/// <para>A receipt only has to outlive the window in which its source may re-observe the same
/// signal and retry it, which is minutes. The default keeps a week so an operator can still trace
/// why a workflow did or did not fire. Delete-where without archive: receipts are operational
/// bookkeeping, not audit-grade — compliance retention lives in
/// <see cref="AuditLogRetentionService"/>.</para>
///
/// <para>Checkpoints are deliberately not swept. There is exactly one row per trigger node, it is
/// updated in place, and deleting it would make the source seed a fresh cursor on its next start.
/// Leader-only.</para>
///
/// <para>Config: <see cref="TriggerReceiptsRetentionOptions"/> (<c>Retention:TriggerReceipts:*</c> —
/// Enabled=true, MaxAgeDays=7, IntervalMinutes=360).</para>
/// </summary>
public class TriggerReceiptRetentionService : LeaderGatedRetentionService
{
    // Resolved per pass from the live monitor — never cached across passes.
    private TriggerReceiptsRetentionOptions Opts => _opts.CurrentValue.TriggerReceipts;

    public TriggerReceiptRetentionService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<RetentionOptions> opts,
        NodePilot.Core.Interfaces.IClusterStateProvider cluster,
        ILogger<TriggerReceiptRetentionService> logger,
        IDatabaseAvailability availability)
        : base(scopeFactory, opts, cluster, logger, availability)
    {
    }

    protected override string ServiceName => nameof(TriggerReceiptRetentionService);
    protected override string MetricServiceTag => "trigger_receipts";
    protected override TimeSpan WarmUpDelay => TimeSpan.FromSeconds(60);
    protected override int MinIntervalMinutes => 1;
    protected override int ConfiguredIntervalMinutes => Opts.IntervalMinutes;

    internal override async Task RunIterationAsync(CancellationToken ct)
    {
        // Hot-reload: a live toggle to Enabled=false parks the sweep instead of killing the
        // service, so flipping back to true later takes effect without a restart.
        var o = Opts;
        if (!o.Enabled)
        {
            _logger.LogDebug(
                "TriggerReceiptRetentionService pass skipped (Retention:TriggerReceipts:Enabled=false).");
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
                _logger.LogInformation(
                    "Pruned {Count} trigger delivery receipts older than {Days}d.", deleted, maxAgeDays);
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
            _logger.LogError(ex, "Trigger-receipt retention sweep failed — retrying on next interval.");
        }
    }

    // Internal so unit tests can drive a single pass without the warm-up / interval.
    internal async Task<int> PurgeOnceAsync(int maxAgeDays, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        return await db.TriggerDeliveryReceipts.Where(r => r.ReceivedAt < cutoff)
            .ExecuteDeleteAsync(ct);
    }
}
