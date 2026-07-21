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
/// Periodically trims the <c>WorkflowVersions</c> history table so a frequently-edited
/// workflow (CI/CD-driven updates, iterative UI edits) does not grow its history unbounded.
///
/// <para>
/// Unlike <see cref="ExecutionRetentionService"/> this uses a <em>count-based</em> policy
/// per workflow, not an age-based one. Rationale: a rarely-edited workflow's three-year-old
/// version may still be the most recent pre-bug snapshot a user wants to roll back to —
/// deleting it by age would destroy genuine value. Capping by count preserves the tail
/// (latest N edits, whatever their age) for every workflow independently.
/// </para>
///
/// <para>
/// The current live row in <c>Workflows</c> is <em>not</em> affected — <c>WorkflowVersions</c>
/// holds only historical snapshots. A workflow whose history is trimmed to zero still has
/// its current definition intact and rollback-able (via the "current" entry that the
/// versions endpoint synthesizes from <c>Workflows</c>).
/// </para>
///
/// Config (appsettings.json):
///   "Retention": {
///     "WorkflowVersions": {
///       "Enabled": true,
///       "MaxVersionsPerWorkflow": 50,
///       "IntervalMinutes": 1440,
///       "BatchSize": 500
///     }
///   }
/// </summary>
public class WorkflowVersionsRetentionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    // Hot-reload: hold the live monitor (not a cached snapshot) so a config edit of
    // Retention:WorkflowVersions:* takes effect on the next sweep pass without a restart.
    private readonly IOptionsMonitor<RetentionOptions> _opts;
    private readonly NodePilot.Core.Interfaces.IClusterStateProvider _cluster;
    private readonly ILogger<WorkflowVersionsRetentionService> _logger;

    // Resolved per pass from the live monitor — never cached across passes.
    private WorkflowVersionsRetentionOptions Opts => _opts.CurrentValue.WorkflowVersions;

    public WorkflowVersionsRetentionService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<RetentionOptions> opts,
        NodePilot.Core.Interfaces.IClusterStateProvider cluster,
        ILogger<WorkflowVersionsRetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _opts = opts;
        _cluster = cluster;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Match ExecutionRetentionService's warm-up delay so we don't hammer the DB during
        // the cold-start window where the migration bootstrap + TriggerOrchestrator do their
        // initial work.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation("WorkflowVersionsRetentionService started (hot-reload: per-pass config).");

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

        _logger.LogInformation("WorkflowVersionsRetentionService stopped.");
    }

    /// <summary>
    /// Exactly one sweep iteration: reads the live config, skips when disabled, else runs one
    /// <see cref="PurgeOnceAsync"/> pass + heartbeat. No <c>Task.Delay</c> — the loop in
    /// <see cref="ExecuteAsync"/> owns the inter-pass spacing. Internal so unit tests can drive
    /// a single pass (incl. the hot-reload Enabled-toggle path) without the 2-minute warm-up.
    /// </summary>
    internal async Task RunIterationAsync(CancellationToken ct)
    {
        // Hot-reload: a live toggle to Enabled=false parks the sweep instead of killing the
        // service, so flipping back to true later takes effect without a restart.
        var o = Opts;
        if (!o.Enabled)
        {
            _logger.LogDebug("WorkflowVersionsRetentionService pass skipped (Retention:WorkflowVersions:Enabled=false).");
            return;
        }

        var maxVersions = Math.Max(1, o.MaxVersionsPerWorkflow);
        var intervalMinutes = Math.Max(1, o.IntervalMinutes);
        var batchSize = Math.Max(10, o.BatchSize);

        try
        {
            var sw = Stopwatch.StartNew();
            var deleted = await PurgeOnceAsync(maxVersions, batchSize, ct);
            sw.Stop();
            if (deleted > 0)
                _logger.LogInformation("WorkflowVersions retention deleted {Count} old history rows.", deleted);
            var tags = new TagList { new("nodepilot.retention.service", "workflow_versions") };
            SchedulerMetrics.RetentionRowsDeleted.Add(deleted, tags);
            SchedulerMetrics.RetentionSweepDuration.Record(sw.Elapsed.TotalMilliseconds, tags);
            await HeartbeatAsync(intervalMinutes, $"ok: {deleted} deleted", ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var errTags = new TagList { new("nodepilot.retention.service", "workflow_versions") };
            SchedulerMetrics.RetentionSweepErrors.Add(1, errTags);
            _logger.LogError(ex, "WorkflowVersions retention pass failed — will retry on next interval.");
        }
    }

    private async Task HeartbeatAsync(int intervalMinutes, string status, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        await SystemHealthWriter.BeatAsync(db, "WorkflowVersionsRetentionService",
            expectedIntervalSeconds: intervalMinutes * 60, status: status, ct: ct);
    }

    // Exposed as internal so unit tests can drive a single pass without waiting the warm-up
    // or a full interval.
    internal async Task<int> PurgeOnceAsync(int maxVersionsPerWorkflow, int batchSize, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();

        // Strategy: for each WorkflowId, find the (Version) of the `maxVersionsPerWorkflow`-th
        // newest row. Everything strictly older than that threshold is a delete candidate.
        // Done per-workflow so one prolific editor doesn't eat another workflow's history.
        //
        // Two-pass query keeps SQL simple and portable (no ROW_NUMBER window pattern required
        // for SQLite + SQL Server compatibility): first learn the cutoff per workflow, then
        // bulk-delete below that cutoff in batches.
        var cutoffs = await db.WorkflowVersions
            .GroupBy(v => v.WorkflowId)
            .Where(g => g.Count() > maxVersionsPerWorkflow)
            .Select(g => new
            {
                WorkflowId = g.Key,
                // The version at position maxVersionsPerWorkflow (0-indexed) in descending order
                // is the oldest row we want to keep. Everything strictly below it goes.
                MinKeep = g.OrderByDescending(v => v.Version)
                           .Skip(maxVersionsPerWorkflow - 1)
                           .Select(v => v.Version)
                           .FirstOrDefault()
            })
            .ToListAsync(ct);

        if (cutoffs.Count == 0) return 0;

        int totalDeleted = 0;
        foreach (var c in cutoffs)
        {
            if (ct.IsCancellationRequested) break;
            // Bound per-pass work to batchSize across ALL workflows to keep SQLite
            // transactions short. Resume on the next interval if more is due.
            if (totalDeleted >= batchSize) break;

            var remaining = batchSize - totalDeleted;
            var rowsToDelete = await db.WorkflowVersions
                .Where(v => v.WorkflowId == c.WorkflowId && v.Version < c.MinKeep)
                .OrderBy(v => v.Version)
                .Take(remaining)
                .ToListAsync(ct);

            if (rowsToDelete.Count == 0) continue;
            db.WorkflowVersions.RemoveRange(rowsToDelete);
            totalDeleted += await db.SaveChangesAsync(ct);
        }

        return totalDeleted;
    }
}
