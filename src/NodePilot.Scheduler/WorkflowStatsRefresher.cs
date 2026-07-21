using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Scheduler;

/// <summary>
/// Periodically recomputes <see cref="WorkflowStats"/> rows from the raw
/// <c>WorkflowExecutions</c> + <c>StepExecutions</c> tables. Separates the "expensive
/// aggregation" workload from the hot read paths (`GET /api/stats/dashboard`,
/// `GET /api/workflows`) so list responses stay O(#workflows), not O(#executions).
///
/// <para>Config keys:</para>
/// <list type="bullet">
///   <item><c>Stats:RefreshIntervalMinutes</c> (default 5)</item>
///   <item><c>Stats:WindowDays</c> (default 7) — size of the rolling window the *Window columns cover</item>
/// </list>
///
/// <para>
/// Runs always-on (no opt-in toggle): the work is cheap for a typical workflow count
/// (~1 query per refresh with a GROUP BY), and stale dashboards are worse than the CPU.
/// </para>
/// </summary>
public class WorkflowStatsRefresher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly NodePilot.Core.Interfaces.IClusterStateProvider _cluster;
    private readonly ILogger<WorkflowStatsRefresher> _logger;

    public WorkflowStatsRefresher(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        NodePilot.Core.Interfaces.IClusterStateProvider cluster,
        ILogger<WorkflowStatsRefresher> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _cluster = cluster;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Short warm-up so the patcher + other startup work finish first.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation("WorkflowStatsRefresher started (hot-reload: per-pass config).");

        while (!stoppingToken.IsCancellationRequested)
        {
            // HA gate: leader-only.
            if (!_cluster.IsLeader)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            // Hot-reload: re-read config every pass so a live edit of Stats:* takes effect
            // without a restart.
            var intervalMinutes = Math.Max(1, _config.GetValue("Stats:RefreshIntervalMinutes", 5));
            var windowDays = Math.Max(1, _config.GetValue("Stats:WindowDays", 7));
            var interval = TimeSpan.FromMinutes(intervalMinutes);

            try
            {
                var refreshed = await RefreshOnceAsync(windowDays, stoppingToken);
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
                await SystemHealthWriter.BeatAsync(db, "WorkflowStatsRefresher",
                    expectedIntervalSeconds: intervalMinutes * 60,
                    status: $"ok: {refreshed} workflows refreshed", ct: stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Stats refresh failed — will retry on next interval.");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Performs one full refresh pass: upserts a <see cref="WorkflowStats"/> row for every
    /// workflow. Returns the number of workflows whose stats were written.
    /// Uses 4 aggregation queries instead of N+1 per workflow.
    /// </summary>
    internal async Task<int> RefreshOnceAsync(int windowDays, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();

        var windowStart = DateTime.UtcNow.AddDays(-windowDays);
        var workflowIds = await db.Workflows.Select(w => w.Id).ToListAsync(ct);
        var existing = (await db.WorkflowStats.ToListAsync(ct)).ToDictionary(s => s.WorkflowId);

        // Query 1: total count + last execution timestamp per workflow
        var allTimeSummary = (await db.WorkflowExecutions.AsNoTracking()
            .GroupBy(e => e.WorkflowId)
            .Select(g => new { WorkflowId = g.Key, Total = g.Count(), LastAt = g.Max(e => e.StartedAt) })
            .ToListAsync(ct))
            .ToDictionary(r => r.WorkflowId);

        // Query 2: in-window status counts per workflow
        var windowByWorkflow = (await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.StartedAt >= windowStart)
            .GroupBy(e => new { e.WorkflowId, e.Status })
            .Select(g => new { g.Key.WorkflowId, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct))
            .GroupBy(r => r.WorkflowId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Query 3: last success/failure timestamp per workflow (all time)
        var lastByWorkflowStatus = (await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Status == ExecutionStatus.Succeeded || e.Status == ExecutionStatus.Failed)
            .GroupBy(e => new { e.WorkflowId, e.Status })
            .Select(g => new { g.Key.WorkflowId, g.Key.Status, LastAt = g.Max(e => e.StartedAt) })
            .ToListAsync(ct))
            .ToDictionary(r => (r.WorkflowId, r.Status));

        // Query 4: succeeded duration samples in window for percentile calculation
        var durationsByWorkflow = (await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.StartedAt >= windowStart
                        && e.Status == ExecutionStatus.Succeeded
                        && e.CompletedAt != null)
            .Select(e => new { e.WorkflowId, e.StartedAt, CompletedAt = e.CompletedAt!.Value })
            .ToListAsync(ct))
            .GroupBy(r => r.WorkflowId)
            .ToDictionary(g => g.Key, g => g
                .Select(r => (r.CompletedAt - r.StartedAt).TotalMilliseconds)
                .OrderBy(d => d)
                .ToList());

        int processed = 0;
        foreach (var wfId in workflowIds)
        {
            allTimeSummary.TryGetValue(wfId, out var allTime);
            windowByWorkflow.TryGetValue(wfId, out var winCounts);
            durationsByWorkflow.TryGetValue(wfId, out var durations);
            lastByWorkflowStatus.TryGetValue((wfId, ExecutionStatus.Succeeded), out var lastSucc);
            lastByWorkflowStatus.TryGetValue((wfId, ExecutionStatus.Failed), out var lastFail);

            durations ??= [];
            double? p50 = Percentile(durations, 0.50);
            double? p95 = Percentile(durations, 0.95);

            if (!existing.TryGetValue(wfId, out var row))
            {
                row = new WorkflowStats { WorkflowId = wfId };
                db.WorkflowStats.Add(row);
            }

            row.TotalExecutions = allTime?.Total ?? 0;
            row.WindowDays = windowDays;
            row.SucceededWindow = winCounts?.FirstOrDefault(r => r.Status == ExecutionStatus.Succeeded)?.Count ?? 0;
            row.FailedWindow = winCounts?.FirstOrDefault(r => r.Status == ExecutionStatus.Failed)?.Count ?? 0;
            row.CancelledWindow = winCounts?.FirstOrDefault(r => r.Status == ExecutionStatus.Cancelled)?.Count ?? 0;
            row.AvgDurationMsWindow = durations.Count > 0 ? durations.Average() : null;
            row.P50DurationMsWindow = p50;
            row.P95DurationMsWindow = p95;
            row.LastExecutionAt = allTime?.LastAt;
            row.LastSuccessAt = lastSucc?.LastAt;
            row.LastFailureAt = lastFail?.LastAt;
            row.RefreshedAt = DateTime.UtcNow;
            processed++;
        }

        // Remove orphan stats rows (workflow deleted since last refresh).
        var orphanIds = existing.Keys.Except(workflowIds).ToList();
        if (orphanIds.Count > 0)
        {
            var orphans = await db.WorkflowStats.Where(s => orphanIds.Contains(s.WorkflowId)).ToListAsync(ct);
            db.WorkflowStats.RemoveRange(orphans);
        }

        await db.SaveChangesAsync(ct);
        return processed;
    }

    /// <summary>
    /// Linear-interpolation percentile over a pre-sorted ascending list. Returns null
    /// for empty input rather than throwing — stats "no data yet" are legitimate and
    /// should show as null in the UI, not as a default zero that's visually indistinguishable
    /// from "ran in 0 ms".
    /// </summary>
    private static double? Percentile(List<double> sortedAsc, double q)
    {
        if (sortedAsc.Count == 0) return null;
        if (sortedAsc.Count == 1) return sortedAsc[0];
        var pos = q * (sortedAsc.Count - 1);
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        if (lo == hi) return sortedAsc[lo];
        var frac = pos - lo;
        return sortedAsc[lo] + frac * (sortedAsc[hi] - sortedAsc[lo]);
    }
}
