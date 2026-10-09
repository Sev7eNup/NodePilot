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
/// under-reported, and a window shorter than <see cref="MinimumWindowHours"/> would be
/// over-reported. The caller then falls back to computing from raw rows: slower, but never wrong.
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
    /// Buckets start on the hour, so a window read from them includes up to one extra hour.
    /// Shorter windows are computed from raw rows.
    /// </summary>
    internal const int MinimumWindowHours = 24;

    /// <summary>
    /// True when the buckets cover everything from <paramref name="since"/> onwards and are being
    /// kept current. Buckets from a rollup that is disabled or keeps failing would silently miss
    /// every hour since its last pass.
    ///
    /// <para>A finished backfill covers every window: it stops at the oldest execution or at the
    /// retention horizon, so no older execution exists that a window could miss.</para>
    /// </summary>
    public async Task<bool> CoversAsync(DateTime since, CancellationToken ct)
    {
        var state = await db.ExecutionStatsRollupStates.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == ExecutionStatsRollupService.StateRowId, ct);
        return state is { CoverageStartUtc: { } start, CoverageEndUtc: not null }
               && (state.BackfillComplete || start <= since)
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
        if (windowHours < MinimumWindowHours) return null;
        var now = DateTime.UtcNow;
        var windowStart = now.AddHours(-windowHours);
        var since = ExecutionStatsRollupService.Truncate(windowStart);
        if (!await CoversAsync(since, ct)) return null;

        var scoped = Scoped(accessible, since);
        if (scoped is null) return new DashboardWindowAggregates(0, windowStart, [], new ExecutionRetryStats(0, 0));

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

        var slots = rows
            .GroupBy(r => r.HourUtc)
            .Select(g => new ExecutionSlotAggregate(
                g.Key,
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
        return new DashboardWindowAggregates(allTime, windowStart, slots, retry);
    }

    /// <summary>
    /// Top failure causes for the window from buckets, merged the same way the live path merges
    /// them. Null if the window is not covered.
    /// </summary>
    public async Task<FailureCausesResponse?> ReadFailureCausesAsync(
        AccessibleFolderSet accessible, int windowHours, CancellationToken ct)
    {
        if (windowHours < MinimumWindowHours) return null;
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

    /// <summary>
    /// Median and P95 run duration for 24 chart buckets from the hourly duration histograms, or null
    /// if the window is not covered. The grid ends with the current hour and every bucket spans
    /// whole hours, so the window can start up to an hour later than now minus its length.
    /// </summary>
    public async Task<List<DurationBucket>?> ReadDurationBucketsAsync(
        AccessibleFolderSet accessible, int windowHours, Guid? workflowId, DateTime nowUtc, CancellationToken ct)
    {
        const int bucketCount = 24;
        if (windowHours < MinimumWindowHours || windowHours % bucketCount != 0) return null;
        var hoursPerBucket = windowHours / bucketCount;
        var start = ExecutionStatsRollupService.Truncate(nowUtc).AddHours(1 - windowHours);
        if (!await CoversAsync(start, ct)) return null;

        var histograms = Enumerable.Range(0, bucketCount).Select(_ => new DurationHistogram()).ToArray();
        var scoped = Scoped(accessible, start);
        if (scoped is not null)
        {
            var query = scoped.Where(s => s.DurationHistogram != "");
            if (workflowId is { } id) query = query.Where(s => s.WorkflowId == id);
            var rows = await query.Select(s => new { s.HourUtc, s.DurationHistogram }).ToListAsync(ct);
            foreach (var row in rows)
            {
                var index = (int)(row.HourUtc - start).TotalHours / hoursPerBucket;
                if (index is >= 0 and < bucketCount) histograms[index].Add(row.DurationHistogram);
            }
        }

        return histograms
            .Select((h, i) => new DurationBucket(start.AddHours(i * hoursPerBucket), (int)h.Count, h.Median, h.P95))
            .ToList();
    }
}
