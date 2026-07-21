using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Scheduler.SystemAlerts.Sources;

/// <summary>
/// Per-service health source: one observation per background service, keyed by service name, exposing
/// <c>staleSeconds</c> (heartbeat age) and <c>expectedIntervalSeconds</c>. A policy decides staleness
/// (e.g. <c>staleSeconds &gt; 180</c>), replacing the old fixed 3×-interval formula.
/// </summary>
public sealed class ServiceStaleSource : ISystemAlertSource
{
    public string SourceId => "service-stale";

    public SystemAlertSourceDescriptor Describe() => new(
        SourceId, SystemAlertCategory.Health, SystemAlertScopeCapability.GlobalOnly, NotificationSeverity.Warning,
        Fields:
        [
            SystemAlertField.Of("staleSeconds", SystemAlertFieldType.Number, unit: "seconds"),
            SystemAlertField.Of("expectedIntervalSeconds", SystemAlertFieldType.Number, unit: "seconds"),
        ],
        Parameters: [],
        Presets: [new SystemAlertPreset("stale", NotificationSeverity.Warning, 0, SystemAlertConditions.Compare("staleSeconds", ">", "180"))]);

    public async Task<bool> IsAvailableAsync(NodePilotDbContext db, CancellationToken ct)
        => await db.SystemHealth.AsNoTracking().AnyAsync(ct);

    public async Task<IReadOnlyList<SystemAlertObservation>> ObserveAsync(NodePilotDbContext db, SystemAlertQuery query, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var rows = await db.SystemHealth.AsNoTracking()
            .Select(h => new { h.ServiceName, h.LastHeartbeatAt, h.ExpectedIntervalSeconds })
            .OrderBy(h => h.ServiceName)
            .ToListAsync(ct);

        return rows.Select(h =>
        {
            var staleSeconds = (long)Math.Max(0, (now - h.LastHeartbeatAt).TotalSeconds);
            return new SystemAlertObservation(SourceId, h.ServiceName, NotificationSeverity.Warning,
                $"Service heartbeat age: {h.ServiceName} ({staleSeconds}s)",
                $"Background service '{h.ServiceName}' last reported {staleSeconds}s ago (expected every {h.ExpectedIntervalSeconds}s).",
                "/",
                new Dictionary<string, object?> { ["staleSeconds"] = staleSeconds, ["expectedIntervalSeconds"] = (long)h.ExpectedIntervalSeconds },
                TargetMachine: null, SignalValue: staleSeconds);
        }).ToList();
    }
}
