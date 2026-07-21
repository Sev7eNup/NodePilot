using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Notifications;

namespace NodePilot.Scheduler.Notifications;

/// <summary>
/// Queued-long collector: scans still-pending executions older than <see cref="Threshold"/>
/// and emits one workflow-scoped <see cref="NotificationEventType.ExecutionQueuedLong"/> per
/// execution. EventKey shape: <c>queuedlong:{guidN}</c> (existence-check dedups across passes).
/// </summary>
internal sealed class QueuedLongExecutionCollector : INotificationCollector
{
    private readonly IConfiguration _configuration;

    // A pending execution older than this fires ExecutionQueuedLong (once per execution).
    // Initialised from Alerting:QueuedLongSeconds (default 300); hot-reload overlaid per pass;
    // settable in tests via the dispatcher's forwarding property.
    internal TimeSpan Threshold { get; set; }

    public QueuedLongExecutionCollector(IConfiguration configuration)
    {
        _configuration = configuration;
        Threshold = TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue("Alerting:QueuedLongSeconds", 300)));
    }

    public async Task<NotificationCollection?> CollectAsync(
        NodePilotDbContext db, IReadOnlyList<NotificationRule> enabledRules, DateTime now, CancellationToken ct)
    {
        // Hot-reload: overlay the threshold every pass (only when the key is explicitly set).
        var seconds = _configuration.GetValue<int?>("Alerting:QueuedLongSeconds");
        if (seconds.HasValue) Threshold = TimeSpan.FromSeconds(Math.Max(1, seconds.Value));

        var rules = enabledRules
            .Where(r => NotificationRuleSemantics.RuleWants(r, NotificationEventType.ExecutionQueuedLong))
            .ToList();
        if (rules.Count == 0) return null;

        var cutoff = now - Threshold;
        var batch = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Status == ExecutionStatus.Pending && e.StartedAt <= cutoff)
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
        // Shape: queuedlong:{guidN}. Re-derive from the pending row.
        var parts = eventKey.Split(':');
        if (parts.Length != 2 || parts[0] != "queuedlong" || !Guid.TryParse(parts[1], out var execId)) return null;

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
            EventType: NotificationEventType.ExecutionQueuedLong,
            Severity: NotificationSeverity.Warning,
            EventKey: $"queuedlong:{row.Id:N}",
            WorkflowId: row.WorkflowId,
            WorkflowName: row.WorkflowName,
            FolderId: row.FolderId,
            FolderPath: row.FolderPath,
            ExecutionId: row.Id,
            Status: "Pending",
            ErrorMessage: null,
            DurationMs: elapsedMs,
            OccurredAt: now,
            TriggeredBy: row.TriggeredBy,
            CallDepth: row.ParentExecutionId.HasValue ? 1 : 0,
            IsSubWorkflow: row.ParentExecutionId.HasValue,
            TargetMachine: null,
            SourceKey: null,
            Title: $"Execution queued long: {row.WorkflowName}",
            Summary: $"Execution has been pending for ~{(long)(now - row.StartedAt).TotalMinutes} min.",
            DeepLinkPath: $"/executions/{row.Id}");
    }
}
