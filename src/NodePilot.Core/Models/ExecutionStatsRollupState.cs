namespace NodePilot.Core.Models;

/// <summary>
/// Single-row bookkeeping for <c>ExecutionStatsRollupService</c>: how far the precomputed hourly
/// buckets actually reach.
///
/// <para>
/// The read path needs this to decide honestly whether it may use the buckets. While the initial
/// backfill is still walking backwards through existing history, a window that reaches past
/// <see cref="CoverageStartUtc"/> would silently under-report — so the dashboard falls back to
/// computing from raw rows for those windows. Slow but correct beats fast but wrong.
/// </para>
/// </summary>
public class ExecutionStatsRollupState
{
    /// <summary>Fixed at 1 — this table holds exactly one row.</summary>
    public int Id { get; set; }

    /// <summary>
    /// Oldest hour the buckets cover completely. Null means nothing has been backfilled yet, so no
    /// window may be served from buckets. Moves backwards as the backfill progresses and forwards
    /// again when retention removes old history.
    /// </summary>
    public DateTime? CoverageStartUtc { get; set; }

    /// <summary>
    /// Newest hour that has been rolled up. Everything after it is computed live from raw rows,
    /// which is cheap because it is at most the current hour plus whatever the last pass missed.
    /// </summary>
    public DateTime? CoverageEndUtc { get; set; }

    /// <summary>True once the backfill has reached the retention horizon and stops walking back.</summary>
    public bool BackfillComplete { get; set; }

    public DateTime UpdatedAt { get; set; }
}
