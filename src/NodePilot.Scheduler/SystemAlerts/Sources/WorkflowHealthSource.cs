using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Scheduler.SystemAlerts.Sources;

/// <summary>
/// Per-workflow quality source reading the pre-aggregated <see cref="WorkflowStats"/> row (one indexed row
/// per workflow, refreshed by the stats refresher). Exposes a rolling-window <c>failureRatePct</c> and
/// <c>p95DurationMs</c> — enabling "workflow X fails &gt; 20%" or "p95 latency doubled" policies that no
/// execution-level source can cheaply express. Only workflows with executions in the window are reported.
/// </summary>
public sealed class WorkflowHealthSource : ISystemAlertSource
{
    public string SourceId => "workflow-health";

    public SystemAlertSourceDescriptor Describe() => new(
        SourceId, SystemAlertCategory.Execution, SystemAlertScopeCapability.WorkflowScoped, NotificationSeverity.Warning,
        Fields:
        [
            SystemAlertField.Of("failureRatePct", SystemAlertFieldType.Number, unit: "percent"),
            SystemAlertField.Of("failedWindow", SystemAlertFieldType.Number, unit: "count"),
            SystemAlertField.Of("succeededWindow", SystemAlertFieldType.Number, unit: "count"),
            SystemAlertField.Of("p95DurationMs", SystemAlertFieldType.Number, unit: "milliseconds"),
            SystemAlertField.Of("avgDurationMs", SystemAlertFieldType.Number, unit: "milliseconds"),
        ],
        Parameters: [],
        Presets:
        [
            new SystemAlertPreset("high-failure-rate", NotificationSeverity.Warning, 0, SystemAlertConditions.Compare("failureRatePct", ">", "20")),
        ]);

    public Task<bool> IsAvailableAsync(NodePilotDbContext db, CancellationToken ct) => db.WorkflowStats.AsNoTracking().AnyAsync(ct);

    public async Task<IReadOnlyList<SystemAlertObservation>> ObserveAsync(NodePilotDbContext db, SystemAlertQuery query, CancellationToken ct)
    {
        var rows = await db.WorkflowStats.AsNoTracking()
            .Where(s => s.TotalExecutions > 0)
            .Join(db.Workflows.AsNoTracking(), s => s.WorkflowId, w => w.Id,
                (s, w) => new { s.WorkflowId, w.Name, w.FolderId, s.SucceededWindow, s.FailedWindow, s.P95DurationMsWindow, s.AvgDurationMsWindow })
            .ToListAsync(ct);

        return rows.Select(r =>
        {
            var denom = r.SucceededWindow + r.FailedWindow;
            var failureRate = denom > 0 ? (long)Math.Round(100.0 * r.FailedWindow / denom) : 0;
            var p95 = (long)Math.Round(r.P95DurationMsWindow ?? 0);
            return new SystemAlertObservation(SourceId, r.WorkflowId.ToString("N"), NotificationSeverity.Warning,
                $"Workflow health: {r.Name} ({failureRate}% failing)",
                $"{r.Name}: {r.FailedWindow} failed / {denom} runs in the window (p95 {p95} ms).",
                $"/workflows/{r.WorkflowId:D}",
                new Dictionary<string, object?>
                {
                    ["failureRatePct"] = failureRate,
                    ["failedWindow"] = r.FailedWindow,
                    ["succeededWindow"] = r.SucceededWindow,
                    ["p95DurationMs"] = p95,
                    ["avgDurationMs"] = (long)Math.Round(r.AvgDurationMsWindow ?? 0),
                },
                WorkflowId: r.WorkflowId, WorkflowName: r.Name, FolderId: r.FolderId, SignalValue: failureRate);
        }).ToList();
    }
}
