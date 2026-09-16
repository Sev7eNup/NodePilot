using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Core.Interfaces;
using NodePilot.Data;

namespace NodePilot.Api.Services;

/// <summary>
/// Reads the dashboard's historical figures from the precomputed hourly buckets.
///
/// <para>
/// Every method answers <c>null</c> when the buckets cannot honestly serve the requested window —
/// while the initial backfill is still walking through existing history, an older window would be
/// under-reported. The caller then falls back to computing from raw rows: slower, but never wrong.
/// </para>
///
/// <para>
/// Buckets are at most one rollup interval old (~1 minute), which is the agreed contract for
/// historical figures. Live counters, heartbeats and the audit feed never come from here.
/// </para>
/// </summary>
internal sealed class DashboardRollupReader(NodePilotDbContext db)
{
    /// <summary>
    /// True when the buckets cover everything from <paramref name="since"/> onwards and are being
    /// kept current. Buckets from a rollup that is disabled or keeps failing would silently miss
    /// every hour since its last pass.
    /// </summary>
    public async Task<bool> CoversAsync(DateTime since, CancellationToken ct)
    {
        var state = await db.ExecutionStatsRollupStates.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == ExecutionStatsRollupService.StateRowId, ct);
        return state is { CoverageStartUtc: { } start, CoverageEndUtc: not null }
               && start <= since
               && state.UpdatedAt >= DateTime.UtcNow - ExecutionStatsRollupService.StaleAfter;
    }

    /// <summary>
    /// Hourly rows for the window, already scoped to what the caller may read. Null when the
    /// buckets do not cover the window.
    /// </summary>
    private IQueryable<Core.Models.ExecutionHourlyStat>? Scoped(AccessibleFolderSet accessible, DateTime since)
    {
        var query = db.ExecutionHourlyStats.AsNoTracking().Where(s => s.HourUtc >= since);
        if (accessible.IsUnrestricted) return query;
        if (accessible.FolderIds.Count == 0) return null;
        // Same shape as FolderScopedQueries uses for executions: the bucket's workflow decides
        // visibility, which is exactly why buckets are stored per workflow.
        return query.Where(s => accessible.FolderIds.Contains(s.Workflow!.FolderId));
    }

    /// <summary>
    /// The window aggregates (all-time count, hourly series, retry ratio) from buckets, or null if
    /// they are not covered.
    /// </summary>
    public async Task<DashboardWindowAggregates?> ReadWindowAggregatesAsync(
        AccessibleFolderSet accessible, int windowHours, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var since = ExecutionStatsRollupService.Truncate(now.AddHours(-windowHours));
        if (!await CoversAsync(since, ct)) return null;

        var scoped = Scoped(accessible, since);
        if (scoped is null) return new DashboardWindowAggregates(0, [], new ExecutionRetryStats(0, 0));

        var rows = await scoped
            .Select(s => new
            {
                s.HourUtc,
                s.TotalCount,
                s.SucceededCount,
                s.FailedCount,
                s.CancelledCount,
                s.RunningCount,
                s.RetriedCount,
                s.FinishedCount,
            })
            .ToListAsync(ct);

        var hourly = rows
            .GroupBy(r => r.HourUtc)
            .Select(g => new HourlyExecutionAggregate(
                g.Key.Year, g.Key.Month, g.Key.Day, g.Key.Hour,
                g.Sum(r => r.TotalCount),
                g.Sum(r => r.SucceededCount),
                g.Sum(r => r.FailedCount),
                g.Sum(r => r.RunningCount),
                g.Sum(r => r.CancelledCount)))
            .ToList();

        // All-time total stays a live COUNT on purpose. Summing buckets would silently report only
        // the covered range — during the initial backfill that is a fraction of the real history,
        // and the window check above only guarantees coverage for THIS window, not for all time.
        // The count is a single index-only aggregate and was never the bottleneck.
        var executions = db.WorkflowExecutions.AsNoTracking().ScopeToAccessibleFolders(accessible);
        var allTime = executions is null ? 0 : await executions.CountAsync(ct);

        var retry = new ExecutionRetryStats(rows.Sum(r => r.FinishedCount), rows.Sum(r => r.RetriedCount));
        return new DashboardWindowAggregates(allTime, hourly, retry);
    }

    /// <summary>
    /// Top failure causes for the window from buckets, merged the same way the live path merges
    /// them. Null if the window is not covered.
    /// </summary>
    public async Task<FailureCausesResponse?> ReadFailureCausesAsync(
        AccessibleFolderSet accessible, int windowHours, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var since = ExecutionStatsRollupService.Truncate(now.AddHours(-windowHours));
        if (!await CoversAsync(since, ct)) return null;

        var query = db.FailureCauseHourlyStats.AsNoTracking().Where(s => s.HourUtc >= since);
        if (!accessible.IsUnrestricted)
        {
            if (accessible.FolderIds.Count == 0) return new FailureCausesResponse(0, [], 0);
            query = query.Where(s => accessible.FolderIds.Contains(s.Workflow!.FolderId));
        }

        var rows = await query
            .Select(s => new { s.MessageHash, s.Message, s.Count, s.LatestExecutionId, s.LatestStartedAt })
            .ToListAsync(ct);

        // Merge across hours and workflows by the hash of the normalised message — the same identity
        // the live path arrives at after normalising, so the resulting groups are identical.
        var merged = new Dictionary<string, FailureCause>(StringComparer.Ordinal);
        var total = 0;
        foreach (var r in rows)
        {
            total += r.Count;
            if (merged.TryGetValue(r.MessageHash, out var existing))
            {
                var newer = r.LatestStartedAt > existing.LatestStartedAt
                    || (r.LatestStartedAt == existing.LatestStartedAt
                        && r.LatestExecutionId.CompareTo(existing.LatestExecutionId) > 0);
                merged[r.MessageHash] = existing with
                {
                    Count = existing.Count + r.Count,
                    LatestExecutionId = newer ? r.LatestExecutionId : existing.LatestExecutionId,
                    LatestStartedAt = newer ? r.LatestStartedAt : existing.LatestStartedAt,
                };
            }
            else
            {
                merged[r.MessageHash] = new FailureCause(
                    string.IsNullOrEmpty(r.Message) ? null : r.Message,
                    r.Count, r.LatestExecutionId, r.LatestStartedAt);
            }
        }

        var top = merged.Values
            .OrderByDescending(g => g.Count)
            .ThenByDescending(g => g.LatestStartedAt)
            .ThenBy(g => g.Message, StringComparer.Ordinal)
            .Take(5)
            .ToList();

        return new FailureCausesResponse(total, top, total - top.Sum(g => g.Count));
    }
}
