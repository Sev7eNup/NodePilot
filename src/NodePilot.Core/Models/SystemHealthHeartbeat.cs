namespace NodePilot.Core.Models;

/// <summary>
/// Liveness marker for a background service. Each hosted service that wants its health
/// observable from the DB upserts a row keyed by <see cref="ServiceName"/> on every successful
/// pass. A missing or stale row signals that the service died silently.
/// <para>
/// Not a time series — one row per service, overwritten. Prometheus holds historical uptime;
/// this table only answers whether the service is alive right now.
/// </para>
/// </summary>
public class SystemHealthHeartbeat
{
    /// <summary>Stable service identifier — doubles as the primary key.</summary>
    public string ServiceName { get; set; } = string.Empty;

    public DateTime LastHeartbeatAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Expected cadence in seconds. Makes "stale" detectable without each monitor having
    /// to hard-code knowledge of how often each service ticks.
    /// </summary>
    public int ExpectedIntervalSeconds { get; set; }

    /// <summary>
    /// Free-text latest status ("ok", "retry backoff: 3 failures", etc.). Optional.
    /// </summary>
    public string? Status { get; set; }
}
