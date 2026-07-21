using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Scheduler.SystemAlerts.Sources;

/// <summary>
/// Event/level source: currently-Running executions and how long they have been running. One observation
/// per in-flight execution, keyed by execution id, exposing <c>runningMinutes</c>. Nothing else alerts on a
/// live hang — the startup reconciler only cleans stuck runs at boot — so a policy like
/// <c>runningMinutes &gt; 30</c> catches a deploy that has been wedged for half an hour.
/// </summary>
public sealed class StuckExecutionSource : ISystemAlertSource
{
    public string SourceId => "execution-stuck";

    public SystemAlertSourceDescriptor Describe() => new(
        SourceId, SystemAlertCategory.Execution, SystemAlertScopeCapability.WorkflowScoped, NotificationSeverity.Warning,
        Fields: [SystemAlertField.Of("runningMinutes", SystemAlertFieldType.Number, unit: "minutes")],
        Parameters: [],
        Presets: [new SystemAlertPreset("long-running", NotificationSeverity.Warning, 0, SystemAlertConditions.Compare("runningMinutes", ">", "30"))]);

    public Task<bool> IsAvailableAsync(NodePilotDbContext db, CancellationToken ct) => Task.FromResult(true);

    public async Task<IReadOnlyList<SystemAlertObservation>> ObserveAsync(NodePilotDbContext db, SystemAlertQuery query, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var rows = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Status == ExecutionStatus.Running && e.CompletedAt == null)
            .Select(e => new { e.Id, e.WorkflowId, e.StartedAt, WorkflowName = e.Workflow.Name, FolderId = e.Workflow.FolderId })
            .ToListAsync(ct);

        return rows.Select(e =>
        {
            var runningMinutes = Math.Max(0, (long)(now - e.StartedAt).TotalMinutes);
            return new SystemAlertObservation(SourceId, e.Id.ToString("N"), NotificationSeverity.Warning,
                $"Execution running {runningMinutes} min: {e.WorkflowName}",
                $"{e.WorkflowName} has been running for {runningMinutes} minutes without completing.",
                $"/executions/{e.Id:D}",
                new Dictionary<string, object?> { ["runningMinutes"] = runningMinutes },
                WorkflowId: e.WorkflowId, WorkflowName: e.WorkflowName, FolderId: e.FolderId, SignalValue: runningMinutes);
        }).ToList();
    }
}
