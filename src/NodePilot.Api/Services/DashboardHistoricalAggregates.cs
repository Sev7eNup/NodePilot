using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Api.Services;

/// <summary>Execution counts for the minute or hour that starts at <paramref name="SlotStartUtc"/>.</summary>
public sealed record ExecutionSlotAggregate(
    DateTime SlotStartUtc, int Total, int Succeeded, int Failed, int Running, int Cancelled);

/// <summary>
/// The dashboard's window-scaled aggregates. These are the values whose cost grows with the
/// selected range, and the reason a 30-day dashboard costs more than a 24-hour one.
/// </summary>
/// <param name="WindowStartUtc">Start of the window at the time these values were computed.</param>
public sealed record DashboardWindowAggregates(
    int ExecutionsTotal,
    DateTime WindowStartUtc,
    IReadOnlyList<ExecutionSlotAggregate> Slots,
    ExecutionRetryStats RetryStats);

/// <summary>Date parts of one slot as the database groups them; turned into a UTC time in C#.</summary>
internal sealed record ExecutionSlotRow(
    int Year, int Month, int Day, int Hour, int Minute,
    int Total, int Succeeded, int Failed, int Running, int Cancelled);

/// <summary>
/// Computes <see cref="DashboardWindowAggregates"/>. Split out of the controller so the same
/// computation can run either inline or inside <see cref="DashboardAggregateCache"/>, which owns a
/// different DbContext.
/// </summary>
internal static class DashboardHistoricalAggregates
{
    private const int OneHourWindow = 1;
    private const int OneHourBucketCount = 30;
    private const int MaxBucketCount = 24;

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
        // Null means the rollup does not cover this window yet (initial backfill still running) or
        // the window is too short for hourly buckets, so we compute from raw executions instead.
        var fromBuckets = await new DashboardRollupReader(db)
            .ReadWindowAggregatesAsync(accessible, windowHours, ct);
        if (fromBuckets is not null) return fromBuckets;

        var now = DateTime.UtcNow;
        var sinceWindow = now.AddHours(-windowHours);
        var execQuery = db.WorkflowExecutions.AsNoTracking().ScopeToAccessibleFolders(accessible);
        if (execQuery is null)
            return new DashboardWindowAggregates(0, sinceWindow, [], new ExecutionRetryStats(0, 0));

        var executionsTotal = await db.WorkflowExecutions.AsNoTracking()
            .ScopeToAccessibleFolders(accessible)!
            .CountAsync(ct);

        var rows = await BuildSlotQuery(execQuery, sinceWindow, now, byMinute: windowHours == OneHourWindow)
            .ToListAsync(ct);
        var slots = rows
            .Select(r => new ExecutionSlotAggregate(
                new DateTime(r.Year, r.Month, r.Day, r.Hour, r.Minute, 0, DateTimeKind.Utc),
                r.Total, r.Succeeded, r.Failed, r.Running, r.Cancelled))
            .ToList();

        var retryStats = await DashboardRetryStats
            .BuildQuery(execQuery, db.StepExecutions.AsNoTracking(), sinceWindow, now)
            .SingleOrDefaultAsync(ct) ?? new ExecutionRetryStats(0, 0);

        return new DashboardWindowAggregates(executionsTotal, sinceWindow, slots, retryStats);
    }

    /// <summary>
    /// Counts executions per minute (<paramref name="byMinute"/>) or per hour, grouped in the
    /// database. All three providers extract the date parts in UTC.
    /// </summary>
    internal static IQueryable<ExecutionSlotRow> BuildSlotQuery(
        IQueryable<WorkflowExecution> executions, DateTime since, DateTime now, bool byMinute)
    {
        var inWindow = executions.Where(e => e.StartedAt >= since && e.StartedAt <= now);
        if (byMinute)
        {
            return inWindow
                .GroupBy(e => new { e.StartedAt.Year, e.StartedAt.Month, e.StartedAt.Day, e.StartedAt.Hour, e.StartedAt.Minute })
                .Select(g => new ExecutionSlotRow(
                    g.Key.Year, g.Key.Month, g.Key.Day, g.Key.Hour, g.Key.Minute,
                    g.Count(),
                    g.Count(e => e.Status == ExecutionStatus.Succeeded),
                    g.Count(e => e.Status == ExecutionStatus.Failed),
                    g.Count(e => e.Status == ExecutionStatus.Running),
                    g.Count(e => e.Status == ExecutionStatus.Cancelled)));
        }

        return inWindow
            .GroupBy(e => new { e.StartedAt.Year, e.StartedAt.Month, e.StartedAt.Day, e.StartedAt.Hour })
            .Select(g => new ExecutionSlotRow(
                g.Key.Year, g.Key.Month, g.Key.Day, g.Key.Hour, 0,
                g.Count(),
                g.Count(e => e.Status == ExecutionStatus.Succeeded),
                g.Count(e => e.Status == ExecutionStatus.Failed),
                g.Count(e => e.Status == ExecutionStatus.Running),
                g.Count(e => e.Status == ExecutionStatus.Cancelled)));
    }

    /// <summary>
    /// Lays out the chart buckets for a window and adds each slot to the bucket it falls in.
    /// The one-hour window uses 30 two-minute buckets from the whole minute, so it draws the same
    /// area chart as the longer windows; those use at most 24 buckets.
    /// </summary>
    internal static List<HourBucket> BuildBuckets(
        DateTime windowStartUtc, int windowHours, IEnumerable<ExecutionSlotAggregate> slots)
    {
        var oneHour = windowHours == OneHourWindow;
        var count = oneHour ? OneHourBucketCount : Math.Min(windowHours, MaxBucketCount);
        var width = TimeSpan.FromMinutes(windowHours * 60 / count);
        // Minute slots only fall cleanly into buckets that start on a whole minute.
        var start = oneHour
            ? windowStartUtc.AddTicks(-(windowStartUtc.Ticks % TimeSpan.TicksPerMinute))
            : windowStartUtc;

        var buckets = Enumerable.Range(0, count)
            .Select(i => new HourBucket(start + i * width, 0, 0, 0))
            .ToList();
        foreach (var slot in slots)
        {
            // The first slot can start before the window and the current slot past the last
            // bucket; both belong to the window.
            var idx = Math.Clamp((int)Math.Floor((slot.SlotStartUtc - start) / width), 0, count - 1);
            var b = buckets[idx];
            buckets[idx] = b with
            {
                Succeeded = b.Succeeded + slot.Succeeded,
                Failed = b.Failed + slot.Failed,
                Cancelled = b.Cancelled + slot.Cancelled,
            };
        }
        return buckets;
    }
}
