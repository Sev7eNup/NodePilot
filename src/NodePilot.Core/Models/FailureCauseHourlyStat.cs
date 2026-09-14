namespace NodePilot.Core.Models;

/// <summary>
/// One normalised failure message, for one workflow, in one UTC hour — precomputed by
/// <c>ExecutionStatsRollupService</c> for <c>GET /api/stats/failure-causes</c>.
///
/// <para>
/// That endpoint is the most expensive on the dashboard: it groups failed runs by their error text,
/// an unbounded column, and on SQL Server additionally collates every row. Precomputing it per hour
/// turns the request into a sum over a few thousand short rows.
/// </para>
///
/// <para>
/// Normalisation (secret redaction, replacing GUIDs and timestamps with placeholders) still happens
/// in C#, exactly as the live path does it — the rollup runs the same code and stores the result, so
/// grouping semantics are unchanged. That is why the message cannot simply be grouped in SQL.
/// </para>
///
/// <para>
/// Keyed per workflow so the read path can apply folder-RBAC by joining <c>Workflow.FolderId</c>,
/// the same way the live query scopes its executions.
/// </para>
/// </summary>
public class FailureCauseHourlyStat
{
    /// <summary>Start of the hour, UTC, truncated. Part of the composite key.</summary>
    public DateTime HourUtc { get; set; }

    /// <summary>The workflow the failures belong to. Part of the composite key.</summary>
    public Guid WorkflowId { get; set; }

    /// <summary>
    /// SHA-256 (hex) of the normalised message. Part of the composite key, and the identity used
    /// when merging buckets across hours and workflows — the same identity the live path arrives at
    /// after normalising.
    /// </summary>
    public string MessageHash { get; set; } = string.Empty;

    /// <summary>
    /// The normalised message, stored in full because it is returned verbatim in the API response.
    /// Null/empty means the run failed without any recorded message — a legitimate group the live
    /// path also produces.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>Failed runs in this hour, for this workflow, carrying this normalised message.</summary>
    public int Count { get; set; }

    /// <summary>Newest failing execution in this bucket, for the drill-down link.</summary>
    public Guid LatestExecutionId { get; set; }

    public DateTime LatestStartedAt { get; set; }

    /// <summary>
    /// False while any execution started in this hour is still open. Mirrors
    /// <see cref="ExecutionHourlyStat.IsFinal"/>: a run started at 10:55 may only fail at 11:30, so
    /// the 10:00 bucket stays provisional until nothing from it is running any more.
    /// </summary>
    public bool IsFinal { get; set; }

    public DateTime ComputedAt { get; set; }

    public Workflow? Workflow { get; set; }
}
