using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Scheduler;

/// <summary>
/// Helper for background services to record a liveness heartbeat. One upsert per pass,
/// keyed by service name, best-effort (swallows exceptions so a transient DB blip doesn't
/// cascade into a service restart).
/// </summary>
internal static class SystemHealthWriter
{
    /// <summary>
    /// Minimum delay between successful writes per service. Background services tick every
    /// 5 s; without debouncing, 7 services × 720 ticks/h = 5 040 SaveChanges/h hammer the
    /// SQLite single-writer lock. Because a service can therefore never refresh its heartbeat
    /// more often than this, <see cref="BeatAsync"/> floors the persisted ExpectedIntervalSeconds
    /// at this value — otherwise a downstream stale-check (the dashboard flags
    /// "age &gt; 3 × ExpectedIntervalSeconds") would trip during a perfectly normal debounce gap
    /// on a healthy service.
    /// </summary>
    private const int DebounceSeconds = 30;

    private static readonly ConcurrentDictionary<string, DateTime> LastWriteAt = new();

    /// <summary>
    /// Time source for the debounce check. Production stays on <see cref="DateTime.UtcNow"/>;
    /// tests substitute a controllable clock to verify the elapsed-window reset path
    /// without sleeping for 30 s. Internal so the InternalsVisibleTo test project can
    /// rebind it; never touch from production code.
    /// </summary>
    internal static Func<DateTime> NowProvider { get; set; } = static () => DateTime.UtcNow;

    public static async Task BeatAsync(NodePilotDbContext db, string serviceName,
        int expectedIntervalSeconds, string? status = null, CancellationToken ct = default)
    {
        // A service can never refresh its heartbeat more often than the debounce window
        // allows, so the *effective* freshness interval is never below DebounceSeconds.
        // Persisting a smaller value (e.g. the TriggerOrchestrator's 5 s tick) lets the
        // dashboard's "age > 3 × ExpectedIntervalSeconds" stale-check trip at 15 s even
        // though the next write only lands at 30 s — flagging a healthy service as "stale"
        // for half of every debounce cycle. Floor it so the stored interval reflects the
        // real write cadence.
        expectedIntervalSeconds = Math.Max(expectedIntervalSeconds, DebounceSeconds);

        // Skip writes that fall inside the debounce window. We reserve the slot up-front
        // so a transient SaveChanges failure does NOT trigger a retry storm — the next
        // attempt happens after the debounce window, exactly the same as a healthy path.
        // A persistent failure means downstream monitors stop seeing beats, which is the
        // correct alarm signal.
        var now = NowProvider();
        if (LastWriteAt.TryGetValue(serviceName, out var lastAt)
            && (now - lastAt).TotalSeconds < DebounceSeconds)
        {
            return;
        }
        LastWriteAt[serviceName] = now;

        try
        {
            // Upsert: if the row exists we patch timestamp + status; otherwise insert.
            // Keyed by PK so EF's ChangeTracker handles concurrency gracefully — a colliding
            // insert from a fresh process after a restart simply wins on the next call.
            var existing = await db.SystemHealth.FindAsync([serviceName], ct);
            if (existing is null)
            {
                db.SystemHealth.Add(new SystemHealthHeartbeat
                {
                    ServiceName = serviceName,
                    LastHeartbeatAt = DateTime.UtcNow,
                    ExpectedIntervalSeconds = expectedIntervalSeconds,
                    Status = status,
                });
            }
            else
            {
                existing.LastHeartbeatAt = DateTime.UtcNow;
                existing.ExpectedIntervalSeconds = expectedIntervalSeconds;
                existing.Status = status;
            }
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Never let heartbeat-write failure kill the service. A missing beat IS the
            // signal we want downstream monitors to pick up — feeding the alert is better
            // than crashing the host.
        }
    }

    /// <summary>
    /// Resets the per-service debounce table. Test-only — use to isolate cases that
    /// run multiple BeatAsync calls inside a single second.
    /// </summary>
    internal static void ResetDebounceForTests() => LastWriteAt.Clear();
}
