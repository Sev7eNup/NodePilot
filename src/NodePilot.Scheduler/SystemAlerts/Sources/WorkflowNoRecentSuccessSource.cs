using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Scheduler.Gauge;

namespace NodePilot.Scheduler.SystemAlerts.Sources;

/// <summary>
/// Workflow-scoped source: one observation per enabled scheduled workflow, keyed by workflow id, exposing
/// <c>minutesSinceSuccess</c> and <c>neverSucceeded</c>. A policy decides "too long" (e.g.
/// <c>minutesSinceSuccess &gt; 1440</c>). Carries workflow/folder identity so it can be folder/workflow-scoped.
/// </summary>
public sealed class WorkflowNoRecentSuccessSource : ISystemAlertSource
{
    // Sentinel minutes-since for a workflow that never succeeded — larger than any realistic threshold.
    private const long NeverSucceededMinutes = 10_000_000;

    public string SourceId => "workflow-no-recent-success";

    public SystemAlertSourceDescriptor Describe() => new(
        SourceId, SystemAlertCategory.Schedule, SystemAlertScopeCapability.WorkflowScoped, NotificationSeverity.Warning,
        Fields:
        [
            SystemAlertField.Of("minutesSinceSuccess", SystemAlertFieldType.Number, unit: "minutes"),
            SystemAlertField.Of("neverSucceeded", SystemAlertFieldType.Boolean),
        ],
        Parameters: [],
        Presets: [new SystemAlertPreset("stale", NotificationSeverity.Warning, 0, SystemAlertConditions.Compare("minutesSinceSuccess", ">", "1440"))]);

    public async Task<bool> IsAvailableAsync(NodePilotDbContext db, CancellationToken ct)
        => (await ScheduledWorkflowSignalHelpers.LoadEnabledScheduledWorkflowsAsync(db, ct)).Count > 0;

    public async Task<IReadOnlyList<SystemAlertObservation>> ObserveAsync(NodePilotDbContext db, SystemAlertQuery query, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var workflows = await ScheduledWorkflowSignalHelpers.LoadEnabledScheduledWorkflowsAsync(db, ct);
        if (workflows.Count == 0) return [];

        var ids = workflows.Select(w => w.Id).ToList();
        var lastSuccess = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => ids.Contains(e.WorkflowId) && e.Status == ExecutionStatus.Succeeded && e.CompletedAt != null)
            .Select(e => new { e.WorkflowId, CompletedAt = e.CompletedAt!.Value })
            .GroupBy(e => e.WorkflowId)
            .Select(g => new { WorkflowId = g.Key, LastSuccessAt = g.Max(e => e.CompletedAt) })
            .ToDictionaryAsync(e => e.WorkflowId, e => e.LastSuccessAt, ct);

        return workflows.Select(w =>
        {
            lastSuccess.TryGetValue(w.Id, out var last);
            var hasSuccess = last != default;
            var minutes = hasSuccess ? Math.Max(0, (long)(now - last).TotalMinutes) : NeverSucceededMinutes;
            return new SystemAlertObservation(SourceId, w.Id.ToString("N"), NotificationSeverity.Warning,
                $"Workflow success age: {w.Name}",
                hasSuccess ? $"{w.Name} last succeeded {minutes} min ago." : $"{w.Name} has no recorded successful execution.",
                $"/workflows/{w.Id:D}",
                new Dictionary<string, object?> { ["minutesSinceSuccess"] = minutes, ["neverSucceeded"] = !hasSuccess },
                WorkflowId: w.Id, WorkflowName: w.Name, FolderId: w.FolderId, FolderPath: w.FolderPath,
                SignalValue: minutes);
        }).ToList();
    }
}
