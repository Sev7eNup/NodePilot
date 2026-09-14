using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Data;

namespace NodePilot.Api.Services;

/// <summary>One hour of execution counts, exactly as the database groups them.</summary>
public sealed record HourlyExecutionAggregate(
    int Year, int Month, int Day, int Hour,
    int Total, int Succeeded, int Failed, int Running, int Cancelled);

/// <summary>
/// The dashboard's window-scaled aggregates. These are the values whose cost grows with the
/// selected range, and the reason a 30-day dashboard costs more than a 24-hour one.
/// </summary>
public sealed record DashboardWindowAggregates(
    int ExecutionsTotal,
    IReadOnlyList<HourlyExecutionAggregate> Hourly,
    ExecutionRetryStats RetryStats);

/// <summary>
/// Computes <see cref="DashboardWindowAggregates"/>. Split out of the controller so the same
/// computation can run either inline or inside <see cref="DashboardAggregateCache"/>, which owns a
/// different DbContext.
/// </summary>
internal static class DashboardHistoricalAggregates
{
    /// <summary>
    /// Computes the aggregates for a window ending now.
    ///
    /// <para>Takes the window length rather than a fixed start/end pair on purpose: the result is
    /// cached and re-computed later by the warm-up, and a captured timestamp would freeze the
    /// window at the moment of the first call.</para>
    /// </summary>
    public static async Task<DashboardWindowAggregates> ComputeAsync(
        NodePilotDbContext db,
        AccessibleFolderSet accessible,
        int windowHours,
        CancellationToken ct)
    {
        // Precomputed buckets first. They answer in milliseconds regardless of how much history
        // exists; the live path below scans raw executions and costs seconds to minutes at scale.
        // Null means the rollup does not cover this window yet (initial backfill still running), so
        // we fall through and compute the honest — if slow — answer.
        var fromBuckets = await new DashboardRollupReader(db)
            .ReadWindowAggregatesAsync(accessible, windowHours, ct);
        if (fromBuckets is not null) return fromBuckets;

        var now = DateTime.UtcNow;
        var sinceWindow = now.AddHours(-windowHours);
        var execQuery = db.WorkflowExecutions.AsNoTracking().ScopeToAccessibleFolders(accessible);
        if (execQuery is null)
            return new DashboardWindowAggregates(0, [], new ExecutionRetryStats(0, 0));

        var executionsTotal = await db.WorkflowExecutions.AsNoTracking()
            .ScopeToAccessibleFolders(accessible)!
            .CountAsync(ct);

        // DB-side GROUP BY (Year/Month/Day/Hour, Status): one aggregated query instead of loading
        // rows into memory. Hour granularity because EF Core compiles e.StartedAt.Year etc.
        // natively on both SqlServer and Postgres; the caller folds wider windows into fewer
        // display buckets.
        var hourly = await execQuery
            .Where(e => e.StartedAt >= sinceWindow)
            .GroupBy(e => new
            {
                e.StartedAt.Year,
                e.StartedAt.Month,
                e.StartedAt.Day,
                e.StartedAt.Hour,
            })
            .Select(g => new HourlyExecutionAggregate(
                g.Key.Year,
                g.Key.Month,
                g.Key.Day,
                g.Key.Hour,
                g.Count(),
                g.Count(e => e.Status == ExecutionStatus.Succeeded),
                g.Count(e => e.Status == ExecutionStatus.Failed),
                g.Count(e => e.Status == ExecutionStatus.Running),
                g.Count(e => e.Status == ExecutionStatus.Cancelled)))
            .ToListAsync(ct);

        var retryStats = await DashboardRetryStats
            .BuildQuery(execQuery, db.StepExecutions.AsNoTracking(), sinceWindow, now)
            .SingleOrDefaultAsync(ct) ?? new ExecutionRetryStats(0, 0);

        return new DashboardWindowAggregates(executionsTotal, hourly, retryStats);
    }
}
