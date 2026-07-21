using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Scheduler.SystemAlerts.Sources;

/// <summary>
/// Event source: terminal workflow executions (Succeeded / Failed / Cancelled), keyed by execution id and
/// scoped to the run's workflow/folder. The <c>lookbackSeconds</c> parameter bounds how far back a raw
/// sample reaches; the evaluator layers per-policy activation-watermark + episode dedup on top so history
/// is not back-alerted on first activation (ADR 0008).
/// </summary>
public sealed class ExecutionResultSource : ISystemAlertSource
{
    private const int DefaultLookbackSeconds = 300;

    public string SourceId => "execution-result";

    public SystemAlertSourceDescriptor Describe() => new(
        SourceId,
        SystemAlertCategory.Execution,
        SystemAlertScopeCapability.WorkflowScoped,
        NotificationSeverity.Warning,
        Fields:
        [
            SystemAlertField.Of("status", SystemAlertFieldType.Enum, enumValues: ["Succeeded", "Failed", "Cancelled"]),
            SystemAlertField.Of("errorMessage", SystemAlertFieldType.String),
            SystemAlertField.Of("durationMs", SystemAlertFieldType.Number, unit: "milliseconds"),
            SystemAlertField.Of("triggeredBy", SystemAlertFieldType.String),
            SystemAlertField.Of("cancelledBy", SystemAlertFieldType.Enum,
                enumValues: ["user", "cancelAll", "failover", "reconciler", "dispatch", "system"]),
            SystemAlertField.Of("isSubWorkflow", SystemAlertFieldType.Boolean),
        ],
        Parameters:
        [
            new SystemAlertParameter("lookbackSeconds", SystemAlertFieldType.Duration,
                Default: DefaultLookbackSeconds, Required: false, Unit: "seconds", Min: 1),
        ],
        Presets:
        [
            new SystemAlertPreset("on-failure", NotificationSeverity.Warning, SustainForSeconds: 0,
                ConditionJson: SystemAlertConditions.Compare("status", "==", "Failed")),
        ]);

    public Task<bool> IsAvailableAsync(NodePilotDbContext db, CancellationToken ct) => Task.FromResult(true);

    public async Task<IReadOnlyList<SystemAlertObservation>> ObserveAsync(
        NodePilotDbContext db, SystemAlertQuery query, CancellationToken ct)
    {
        var lookback = Math.Max(1, query.GetInt("lookbackSeconds", DefaultLookbackSeconds));
        var cutoff = DateTime.UtcNow.AddSeconds(-lookback);

        var rows = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.CompletedAt != null && e.CompletedAt >= cutoff
                && (e.Status == ExecutionStatus.Succeeded
                    || e.Status == ExecutionStatus.Failed
                    || e.Status == ExecutionStatus.Cancelled))
            .Select(e => new
            {
                e.Id,
                e.WorkflowId,
                e.Status,
                e.StartedAt,
                e.CompletedAt,
                e.ErrorMessage,
                e.TriggeredBy,
                e.CancelledBy,
                e.CallDepth,
                WorkflowName = e.Workflow.Name,
                FolderId = e.Workflow.FolderId,
            })
            .OrderBy(e => e.CompletedAt)
            .ToListAsync(ct);

        return rows.Select(e =>
        {
            var durationMs = e.CompletedAt.HasValue
                ? (long)Math.Max(0, (e.CompletedAt.Value - e.StartedAt).TotalMilliseconds)
                : 0;
            var severity = e.Status == ExecutionStatus.Failed ? NotificationSeverity.Warning : NotificationSeverity.Info;
            return new SystemAlertObservation(
                SourceId,
                InstanceKey: e.Id.ToString("N"),
                SeveritySuggestion: severity,
                Title: $"Execution {e.Status}: {e.WorkflowName}",
                Summary: e.Status == ExecutionStatus.Failed && !string.IsNullOrWhiteSpace(e.ErrorMessage)
                    ? $"{e.WorkflowName} failed: {e.ErrorMessage}"
                    : $"{e.WorkflowName} finished {e.Status}.",
                DeepLinkPath: $"/executions/{e.Id:D}",
                Fields: new Dictionary<string, object?>
                {
                    ["status"] = e.Status.ToString(),
                    ["errorMessage"] = e.ErrorMessage ?? "",
                    ["durationMs"] = durationMs,
                    ["triggeredBy"] = e.TriggeredBy ?? "",
                    ["cancelledBy"] = e.CancelledBy ?? "",
                    ["isSubWorkflow"] = e.CallDepth > 0,
                },
                WorkflowId: e.WorkflowId,
                WorkflowName: e.WorkflowName,
                FolderId: e.FolderId,
                OccurredAt: e.CompletedAt,
                SignalValue: durationMs);
        }).ToList();
    }
}
