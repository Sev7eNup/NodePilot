using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Scheduler.SystemAlerts.Sources;

/// <summary>
/// Meta / self-monitoring source: how many alert deliveries have terminally failed in a trailing window
/// (parameter <c>windowMinutes</c>, default 15). This is the only way to notice that the alerting pipeline
/// itself is silently failing to deliver (SMTP down, webhook 5xx) — the alarm about the broken alarms.
/// Field <c>failures</c>.
/// </summary>
public sealed class AlertDeliveryFailureSource : ISystemAlertSource
{
    private const int DefaultWindowMinutes = 15;

    public string SourceId => "alert-delivery-failed";

    public SystemAlertSourceDescriptor Describe() => new(
        SourceId, SystemAlertCategory.Health, SystemAlertScopeCapability.GlobalOnly, NotificationSeverity.Warning,
        Fields: [SystemAlertField.Of("failures", SystemAlertFieldType.Number, unit: "count")],
        Parameters: [new SystemAlertParameter("windowMinutes", SystemAlertFieldType.Duration, Default: DefaultWindowMinutes, Unit: "minutes", Min: 1)],
        Presets: [new SystemAlertPreset("failing", NotificationSeverity.Warning, 0, SystemAlertConditions.Compare("failures", ">", "3"))]);

    public Task<bool> IsAvailableAsync(NodePilotDbContext db, CancellationToken ct) => Task.FromResult(true);

    public async Task<IReadOnlyList<SystemAlertObservation>> ObserveAsync(NodePilotDbContext db, SystemAlertQuery query, CancellationToken ct)
    {
        var windowMinutes = Math.Max(1, query.GetInt("windowMinutes", DefaultWindowMinutes));
        var since = DateTime.UtcNow.AddMinutes(-windowMinutes);
        var failures = await db.NotificationDeliveryAttempts.AsNoTracking()
            .CountAsync(a => a.Status == NotificationDeliveryStatus.Failed && a.CreatedAt >= since, ct);

        return
        [
            new SystemAlertObservation(SourceId, "alert-delivery-failed", NotificationSeverity.Warning,
                $"Alert deliveries failed: {failures} in {windowMinutes}min",
                $"{failures} notification deliveries failed in the last {windowMinutes} minutes — the alerting pipeline may be broken.",
                "/alerts", new Dictionary<string, object?> { ["failures"] = failures }, SignalValue: failures),
        ];
    }
}
