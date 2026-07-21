using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Scheduler.SystemAlerts.Sources;

/// <summary>
/// Global metric source: the count of in-flight executions (Pending + Running). Exposes a single
/// <c>depth</c> field on one constant-key observation ("backlog") — a policy decides the threshold
/// (<c>depth &gt; N</c>) and sustain window, replacing the old fixed <c>Alerting:Gauge:BacklogThreshold</c>.
/// </summary>
public sealed class BacklogSource : ISystemAlertSource
{
    public string SourceId => "backlog";

    public SystemAlertSourceDescriptor Describe() => new(
        SourceId,
        SystemAlertCategory.Queue,
        SystemAlertScopeCapability.GlobalOnly,
        NotificationSeverity.Warning,
        Fields:
        [
            SystemAlertField.Of("depth", SystemAlertFieldType.Number, unit: "count"),
        ],
        Parameters: [],
        Presets:
        [
            new SystemAlertPreset("high", NotificationSeverity.Warning, SustainForSeconds: 60,
                ConditionJson: SystemAlertConditions.Compare("depth", ">", "500")),
        ]);

    public Task<bool> IsAvailableAsync(NodePilotDbContext db, CancellationToken ct) => Task.FromResult(true);

    public async Task<IReadOnlyList<SystemAlertObservation>> ObserveAsync(
        NodePilotDbContext db, SystemAlertQuery query, CancellationToken ct)
    {
        var depth = await db.WorkflowExecutions.AsNoTracking()
            .CountAsync(e => e.Status == ExecutionStatus.Pending || e.Status == ExecutionStatus.Running, ct);

        return
        [
            new SystemAlertObservation(
                SourceId,
                InstanceKey: "backlog",
                SeveritySuggestion: NotificationSeverity.Warning,
                Title: $"Execution backlog: {depth}",
                Summary: $"{depth} executions are pending or running.",
                DeepLinkPath: "/executions",
                Fields: new Dictionary<string, object?> { ["depth"] = depth }),
        ];
    }
}
