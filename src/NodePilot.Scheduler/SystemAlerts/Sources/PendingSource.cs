using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Scheduler.SystemAlerts.Sources;

/// <summary>
/// Global metric source: the count of QUEUED executions (Pending only — running work excluded, unlike
/// <see cref="BacklogSource"/>). One <c>pending</c> field on a constant-key observation.
/// </summary>
public sealed class PendingSource : ISystemAlertSource
{
    public string SourceId => "pending";

    public SystemAlertSourceDescriptor Describe() => new(
        SourceId, SystemAlertCategory.Queue, SystemAlertScopeCapability.GlobalOnly, NotificationSeverity.Warning,
        Fields: [SystemAlertField.Of("pending", SystemAlertFieldType.Number, unit: "count")],
        Parameters: [],
        Presets: [new SystemAlertPreset("high", NotificationSeverity.Warning, 120, SystemAlertConditions.Compare("pending", ">", "40"))]);

    public Task<bool> IsAvailableAsync(NodePilotDbContext db, CancellationToken ct) => Task.FromResult(true);

    public async Task<IReadOnlyList<SystemAlertObservation>> ObserveAsync(NodePilotDbContext db, SystemAlertQuery query, CancellationToken ct)
    {
        var pending = await db.WorkflowExecutions.AsNoTracking().CountAsync(e => e.Status == ExecutionStatus.Pending, ct);
        return
        [
            new SystemAlertObservation(SourceId, "pending", NotificationSeverity.Warning,
                $"Pending queue: {pending}", $"{pending} executions are queued (Pending).", "/executions",
                new Dictionary<string, object?> { ["pending"] = pending }, SignalValue: pending),
        ];
    }
}
