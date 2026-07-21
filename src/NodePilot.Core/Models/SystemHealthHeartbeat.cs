namespace NodePilot.Core.Models;

/// <summary>
/// Per-BackgroundService liveness marker. Each hosted service that wants to make its
/// health observable from the DB upserts a row keyed by <see cref="ServiceName"/> on every
/// successful pass. A missing or stale row (now − <see cref="LastHeartbeatAt"/> > expected
/// interval × 3) is a strong signal that the service has silently died — an alert hook
/// other than "my schedules stopped firing three hours ago and I just noticed".
/// <para>
/// Intentionally NOT a time-series: one row per service, overwritten. Prometheus is the
/// proper home for historical uptime; this table only answers "is it alive right now".
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
