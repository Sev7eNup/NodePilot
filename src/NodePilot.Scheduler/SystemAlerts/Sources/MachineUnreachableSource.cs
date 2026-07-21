using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Scheduler.SystemAlerts.Sources;

/// <summary>
/// Per-machine health source: one observation per managed machine that has a recorded connectivity check,
/// keyed by machine id. Exposes <c>reachable</c> (bool) and <c>staleMinutes</c> (age of the last check).
/// Machines that have never been checked are excluded rather than reported as unreachable — this keeps a
/// rule from the previous, now-removed gauge-based alerting system: "never checked" counts as unknown,
/// not unhealthy.
/// </summary>
public sealed class MachineUnreachableSource : ISystemAlertSource
{
    public string SourceId => "machine-unreachable";

    public SystemAlertSourceDescriptor Describe() => new(
        SourceId,
        SystemAlertCategory.Health,
        SystemAlertScopeCapability.GlobalOnly,
        NotificationSeverity.Critical,
        Fields:
        [
            SystemAlertField.Of("reachable", SystemAlertFieldType.Boolean),
            SystemAlertField.Of("staleMinutes", SystemAlertFieldType.Number, unit: "minutes"),
        ],
        Parameters: [],
        Presets:
        [
            new SystemAlertPreset("unreachable", NotificationSeverity.Critical, SustainForSeconds: 0,
                ConditionJson: SystemAlertConditions.Unary("reachable", "isFalse")),
        ]);

    public async Task<bool> IsAvailableAsync(NodePilotDbContext db, CancellationToken ct)
        => await db.ManagedMachines.AsNoTracking().AnyAsync(m => m.LastConnectivityCheck != null, ct);

    public async Task<IReadOnlyList<SystemAlertObservation>> ObserveAsync(
        NodePilotDbContext db, SystemAlertQuery query, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var machines = await db.ManagedMachines.AsNoTracking()
            .Where(m => m.LastConnectivityCheck != null)
            .Select(m => new { m.Id, m.Name, m.IsReachable, m.LastConnectivityCheck })
            .OrderBy(m => m.Name)
            .ToListAsync(ct);

        return machines.Select(m =>
        {
            var staleMinutes = Math.Max(0, (long)(now - m.LastConnectivityCheck!.Value).TotalMinutes);
            return new SystemAlertObservation(
                SourceId,
                InstanceKey: m.Id.ToString("N"),
                SeveritySuggestion: NotificationSeverity.Critical,
                Title: $"Machine {(m.IsReachable ? "reachable" : "unreachable")}: {m.Name}",
                Summary: m.IsReachable
                    ? $"{m.Name} passed its latest connectivity check ({staleMinutes} min ago)."
                    : $"{m.Name} failed its latest connectivity check ({staleMinutes} min ago).",
                DeepLinkPath: "/machines",
                Fields: new Dictionary<string, object?>
                {
                    ["reachable"] = m.IsReachable,
                    ["staleMinutes"] = staleMinutes,
                },
                TargetMachine: m.Name);
        }).ToList();
    }
}
