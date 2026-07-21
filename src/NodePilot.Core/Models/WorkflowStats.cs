namespace NodePilot.Core.Models;

/// <summary>
/// Pre-computed per-workflow KPIs, refreshed periodically by
/// <c>WorkflowStatsRefresher</c>. Exists so <c>GET /api/stats/dashboard</c> and the
/// <c>GetAll</c> list endpoint don't have to scan the entire <c>WorkflowExecutions</c>
/// table on every request. One row per workflow; overwritten on each refresh.
///
/// <para>
/// The window-based metrics use a rolling wall-clock window (7 days by default) ending
/// at <see cref="RefreshedAt"/>. Consumers should treat the row as stale if the refresh
/// timestamp is older than one refresh cycle.
/// </para>
/// </summary>
public class WorkflowStats
{
    /// <summary>Primary key — matches <see cref="Workflow.Id"/> 1:1 via FK cascade.</summary>
    public Guid WorkflowId { get; set; }

    public int TotalExecutions { get; set; }
    public int SucceededWindow { get; set; }
    public int FailedWindow { get; set; }
    public int CancelledWindow { get; set; }

    /// <summary>Size of the rolling window in days the *Window counters cover.</summary>
    public int WindowDays { get; set; }

    /// <summary>Average duration of succeeded runs in the window, milliseconds.</summary>
    public double? AvgDurationMsWindow { get; set; }

    /// <summary>Percentile durations of succeeded runs in the window, milliseconds.</summary>
    public double? P50DurationMsWindow { get; set; }
    public double? P95DurationMsWindow { get; set; }

    public DateTime? LastExecutionAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public DateTime? LastFailureAt { get; set; }

    public DateTime RefreshedAt { get; set; } = DateTime.UtcNow;

    public Workflow Workflow { get; set; } = null!;
}
