using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Scheduler.SystemAlerts.Sources;

/// <summary>
/// Global metric source: the count of executions cancelled across all workflows within a trailing window
/// (parameter <c>windowMinutes</c>, default 10). Surfaces the global cancel rate a per-workflow flap
/// suppression can't express. Field <c>cancels</c>.
/// </summary>
public sealed class CancelRateSource : ISystemAlertSource
{
    private const int DefaultWindowMinutes = 10;

    public string SourceId => "cancel-rate";

    public SystemAlertSourceDescriptor Describe() => new(
        SourceId, SystemAlertCategory.Queue, SystemAlertScopeCapability.GlobalOnly, NotificationSeverity.Warning,
        Fields: [SystemAlertField.Of("cancels", SystemAlertFieldType.Number, unit: "count")],
        Parameters: [new SystemAlertParameter("windowMinutes", SystemAlertFieldType.Duration, Default: DefaultWindowMinutes, Unit: "minutes", Min: 1)],
        Presets: [new SystemAlertPreset("high", NotificationSeverity.Warning, 0, SystemAlertConditions.Compare("cancels", ">", "10"))]);

    public Task<bool> IsAvailableAsync(NodePilotDbContext db, CancellationToken ct) => Task.FromResult(true);

    public async Task<IReadOnlyList<SystemAlertObservation>> ObserveAsync(NodePilotDbContext db, SystemAlertQuery query, CancellationToken ct)
    {
        var windowMinutes = Math.Max(1, query.GetInt("windowMinutes", DefaultWindowMinutes));
        var since = DateTime.UtcNow.AddMinutes(-windowMinutes);
        var cancels = await db.WorkflowExecutions.AsNoTracking()
            .CountAsync(e => e.Status == ExecutionStatus.Cancelled && e.CompletedAt != null && e.CompletedAt >= since, ct);

        return
        [
            new SystemAlertObservation(SourceId, "cancel-rate", NotificationSeverity.Warning,
                $"Cancellations: {cancels} in {windowMinutes}min", $"{cancels} executions were cancelled in the last {windowMinutes} minutes.",
                "/executions", new Dictionary<string, object?> { ["cancels"] = cancels }, SignalValue: cancels),
        ];
    }
}
