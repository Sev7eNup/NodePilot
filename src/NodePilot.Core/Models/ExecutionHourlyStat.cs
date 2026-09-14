namespace NodePilot.Core.Models;

/// <summary>
/// One workflow's execution counters for one UTC hour, precomputed by
/// <c>ExecutionStatsRollupService</c>.
///
/// <para>
/// Exists because the dashboard's historical aggregates otherwise scan the raw execution table on
/// every request. At millions of rows that costs tens of seconds and no request-scoped cache can fix
/// it — the computation itself is the problem. Rolling completed hours up once turns the dashboard
/// into a sum over at most 720 buckets per workflow, independent of how much history exists.
/// </para>
///
/// <para>
/// Rows are keyed per workflow rather than per hour alone for two reasons: the dashboard needs
/// per-workflow answers (top workflows, failing workflows), and folder-RBAC scoping joins through
/// <c>Workflow.FolderId</c>. A single row per hour could serve neither.
/// </para>
/// </summary>
public class ExecutionHourlyStat
{
    /// <summary>Start of the hour, UTC, truncated to the hour. Part of the composite key.</summary>
    public DateTime HourUtc { get; set; }

    /// <summary>The workflow these counters belong to. Part of the composite key.</summary>
    public Guid WorkflowId { get; set; }

    /// <summary>Executions whose <c>StartedAt</c> falls in this hour, regardless of outcome.</summary>
    public int TotalCount { get; set; }

    public int SucceededCount { get; set; }
    public int FailedCount { get; set; }
    public int CancelledCount { get; set; }

    /// <summary>
    /// Still running when the bucket was computed. Only ever non-zero while
    /// <see cref="IsFinal"/> is false — a finalised hour has no running executions left by
    /// definition.
    /// </summary>
    public int RunningCount { get; set; }

    /// <summary>
    /// Finished executions in this hour that had at least one retried step. Precomputed because the
    /// live form scans the step table on a column with no index.
    /// </summary>
    public int RetriedCount { get; set; }

    /// <summary>Finished executions counted in <see cref="RetriedCount"/>'s denominator.</summary>
    public int FinishedCount { get; set; }

    /// <summary>Sum of run durations in milliseconds, for averages without reading raw rows.</summary>
    public long DurationMsSum { get; set; }

    /// <summary>How many runs contributed to <see cref="DurationMsSum"/>.</summary>
    public int DurationMsCount { get; set; }

    /// <summary>
    /// False while any execution started in this hour is still Pending, Running or Paused.
    ///
    /// <para>
    /// This is what keeps the precomputation honest: an execution is bucketed by
    /// <c>StartedAt</c>, but its status is decided later — a run started at 10:55 can turn
    /// Failed at 11:30. A past hour is therefore not automatically settled, and a non-final hour is
    /// recomputed on every pass until it is.
    /// </para>
    /// </summary>
    public bool IsFinal { get; set; }

    /// <summary>When this row was last written. Diagnostics only.</summary>
    public DateTime ComputedAt { get; set; }

    public Workflow? Workflow { get; set; }
}
