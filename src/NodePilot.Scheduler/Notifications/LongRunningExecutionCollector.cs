using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Notifications;

namespace NodePilot.Scheduler.Notifications;

/// <summary>
/// Long-running collector: scans STILL-RUNNING executions older than <see cref="Threshold"/>
/// and emits an <see cref="NotificationEventType.ExecutionRunningLong"/> context per execution.
/// It is execution-scoped (carries a real WorkflowId → Global/Folders/Workflows scope), NOT a
/// gauge — the per-(rule,route,EventKey) existence-check on <c>runlong:{execId}</c> fires each
/// rule at most once per execution (no per-execution signal-state row to leak). Re-runs every
/// pass so a newly-crossed execution is picked up; finished executions simply drop out of the
/// RUNNING scan.
/// </summary>
internal sealed class LongRunningExecutionCollector : INotificationCollector
{
    private readonly IConfiguration _configuration;

    // A still-running execution older than this fires ExecutionRunningLong (once per execution).
    // Initialised from Alerting:LongRunningSeconds (default 600); hot-reload overlaid per pass;
    // settable in tests via the dispatcher's forwarding property.
    internal TimeSpan Threshold { get; set; }

    public LongRunningExecutionCollector(IConfiguration configuration)
    {
        _configuration = configuration;
        Threshold = TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue("Alerting:LongRunningSeconds", 600)));
    }

    public async Task<NotificationCollection?> CollectAsync(
        NodePilotDbContext db, IReadOnlyList<NotificationRule> enabledRules, DateTime now, CancellationToken ct)
    {
        // Hot-reload: overlay the threshold every pass so a live edit takes effect without a
        // restart. Only overlay when the key is explicitly set — tests that set the property
        // directly with an empty config keep their value.
        var seconds = _configuration.GetValue<int?>("Alerting:LongRunningSeconds");
        if (seconds.HasValue) Threshold = TimeSpan.FromSeconds(Math.Max(1, seconds.Value));

        var rules = enabledRules
            .Where(r => NotificationRuleSemantics.RuleWants(r, NotificationEventType.ExecutionRunningLong))
            .ToList();
        if (rules.Count == 0) return null; // nothing to alert on → skip the running scan entirely

        var cutoff = now - Threshold;
        var batch = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Status == ExecutionStatus.Running && e.StartedAt <= cutoff)
            .OrderBy(e => e.StartedAt)
            .Take(ExecutionEventSupport.ScanBatchSize)
            .Select(e => new ExecRow(
                e.Id, e.WorkflowId, e.Status, e.StartedAt, e.CompletedAt, e.TriggeredBy, e.ErrorMessage,
                e.ParentExecutionId, e.Workflow.Name, e.Workflow.FolderId, e.Workflow.Folder!.Path, e.CancelledBy,
                null))
            .ToListAsync(ct);
        if (batch.Count == 0) return null;

        var contexts = batch.Select(r => BuildContext(r, now)).ToList<NotificationContext>();
        return new NotificationCollection(rules, contexts);
    }

    public async Task<NotificationContext?> TryReconstructContextAsync(
        NodePilotDbContext db, string eventKey, CancellationToken ct)
    {
        // Shape: runlong:{guidN}. Re-derive from the (still-running) row. Without this branch a
        // crash-orphaned runlong: attempt would match no collector and be failed out (lost alert).
        var parts = eventKey.Split(':');
        if (parts.Length != 2 || parts[0] != "runlong" || !Guid.TryParse(parts[1], out var execId)) return null;

        var row = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Id == execId)
            .Select(e => new ExecRow(e.Id, e.WorkflowId, e.Status, e.StartedAt, e.CompletedAt, e.TriggeredBy,
                e.ErrorMessage, e.ParentExecutionId, e.Workflow.Name, e.Workflow.FolderId, e.Workflow.Folder!.Path, e.CancelledBy,
                null))
            .FirstOrDefaultAsync(ct);
        return row is null ? null : BuildContext(row, DateTime.UtcNow);
    }

    private static NotificationContext BuildContext(ExecRow row, DateTime now)
    {
        var elapsedMs = (long)(now - row.StartedAt).TotalMilliseconds;
        return new NotificationContext(
            EventType: NotificationEventType.ExecutionRunningLong,
            Severity: NotificationSeverity.Warning,
            // No time/type segment → one occurrence per execution; the existence-check dedups across passes
            // so a still-running job never re-alerts every 30s.
            EventKey: $"runlong:{row.Id:N}",
            WorkflowId: row.WorkflowId,
            WorkflowName: row.WorkflowName,
            FolderId: row.FolderId,
            FolderPath: row.FolderPath,
            ExecutionId: row.Id,
            Status: "Running",
            ErrorMessage: null,
            DurationMs: elapsedMs,
            OccurredAt: now,
            TriggeredBy: row.TriggeredBy,
            CallDepth: row.ParentExecutionId.HasValue ? 1 : 0,
            IsSubWorkflow: row.ParentExecutionId.HasValue,
            TargetMachine: null,
            SourceKey: null,
            Title: $"Execution running long: {row.WorkflowName}",
            Summary: $"Execution has been running for ~{(long)(now - row.StartedAt).TotalMinutes} min.",
            DeepLinkPath: $"/executions/{row.Id}");
    }
}
