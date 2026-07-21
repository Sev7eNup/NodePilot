using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Notifications;

namespace NodePilot.Scheduler.Notifications;

/// <summary>
/// Terminal-execution collector: scans completed executions since the persisted watermark
/// (keyset cursor on (CompletedAt, Id) with a commit-visibility safety lag), builds one
/// context per row — plus a synthetic <see cref="NotificationEventType.CredentialFailure"/>
/// context when a failure smells credential-shaped and any rule wants that event — and
/// stages the watermark advance on the tracked state row so the pipeline's SaveChanges
/// persists it together with the Pending attempts (crash-safe replay).
/// EventKey shape: <c>exec:{guidN}:{EventTypeName}</c>.
/// </summary>
internal sealed class ExecutionEventCollector : INotificationCollector
{
    // Don't scan executions whose CompletedAt is newer than (now - this). Closes the keyset-cursor
    // race: an execution can be assigned a CompletedAt in-memory and commit a moment later, so a row
    // could otherwise become visible with a timestamp at/below an already-advanced watermark boundary
    // and be skipped forever. The lag keeps the boundary safely behind commit visibility. Settable in tests.
    internal TimeSpan ScanSafetyLag { get; set; } = TimeSpan.FromSeconds(5);

    public async Task<NotificationCollection?> CollectAsync(
        NodePilotDbContext db, IReadOnlyList<NotificationRule> enabledRules, DateTime now, CancellationToken ct)
    {
        var state = await db.NotificationDispatcherStates.FirstOrDefaultAsync(s => s.Id == NotificationDispatcherState.SingletonId, ct);
        if (state is null)
        {
            // First run ever: seed the watermark to "now" so we never back-alert on history.
            state = new NotificationDispatcherState { Id = NotificationDispatcherState.SingletonId, LastCompletedAtSeen = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.NotificationDispatcherStates.Add(state);
            await db.SaveChangesAsync(ct);
            return null;
        }

        var lastTs = state.LastCompletedAtSeen ?? DateTime.MinValue;
        var lastId = state.LastIdSeen ?? Guid.Empty;
        var safeCutoff = DateTime.UtcNow - ScanSafetyLag;

        var batch = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.CompletedAt != null
                && e.CompletedAt <= safeCutoff
                && (e.Status == ExecutionStatus.Succeeded || e.Status == ExecutionStatus.Failed || e.Status == ExecutionStatus.Cancelled)
                && (e.CompletedAt > lastTs || (e.CompletedAt == lastTs && e.Id.CompareTo(lastId) > 0)))
            .OrderBy(e => e.CompletedAt).ThenBy(e => e.Id)
            .Take(ExecutionEventSupport.ScanBatchSize)
            .Select(e => new ExecRow(
                e.Id, e.WorkflowId, e.Status, e.StartedAt, e.CompletedAt, e.TriggeredBy, e.ErrorMessage,
                e.ParentExecutionId, e.Workflow.Name, e.Workflow.FolderId, e.Workflow.Folder!.Path, e.CancelledBy,
                // Gate on Failed in SQL: for Succeeded/Cancelled rows (the common case on a
                // healthy instance) the value is null anyway — don't pay a per-row ORDER BY
                // subquery over StepExecutions for information that gets discarded.
                e.Status == ExecutionStatus.Failed
                    ? e.Steps
                        .Where(s => s.Status == ExecutionStatus.Failed && s.TargetMachine != null)
                        .OrderByDescending(s => s.CompletedAt)
                        .Select(s => s.TargetMachine)
                        .FirstOrDefault()
                    : null))
            .ToListAsync(ct);

        if (batch.Count == 0) return null;
        batch = await ExecutionEventSupport.ResolveTargetMachineNamesAsync(db, batch, ct);

        var contexts = batch.Select(ExecutionEventSupport.BuildContext).ToList<NotificationContext>();
        if (enabledRules.Any(r => NotificationRuleSemantics.RuleWants(r, NotificationEventType.CredentialFailure)))
            contexts.AddRange(batch.Where(ExecutionEventSupport.LooksLikeCredentialFailure)
                .Select(ExecutionEventSupport.BuildCredentialFailureContext));

        // Stage the watermark advance to the last scanned execution. It is persisted together with the
        // Pending attempts inside the pipeline's save, so a crash now leaves replayable rows.
        var lastRow = batch[^1];
        state.LastCompletedAtSeen = lastRow.CompletedAt;
        state.LastIdSeen = lastRow.Id;
        state.UpdatedAt = now;

        // Execution rules aren't pre-filterable by a single event type (a batch spans
        // Succeeded/Failed/Cancelled/CredentialFailure) — pass all enabled rules through.
        return new NotificationCollection(enabledRules, contexts);
    }

    public async Task<NotificationContext?> TryReconstructContextAsync(
        NodePilotDbContext db, string eventKey, CancellationToken ct)
    {
        // Shape: exec:{guidN}:{EventTypeName}. Re-derive the full context from the row.
        var parts = eventKey.Split(':');
        if (parts.Length != 3 || parts[0] != "exec" || !Guid.TryParse(parts[1], out var execId)) return null;
        if (!Enum.TryParse<NotificationEventType>(parts[2], ignoreCase: true, out var eventType)) return null;

        var row = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Id == execId)
            .Select(e => new ExecRow(e.Id, e.WorkflowId, e.Status, e.StartedAt, e.CompletedAt, e.TriggeredBy,
                e.ErrorMessage, e.ParentExecutionId, e.Workflow.Name, e.Workflow.FolderId, e.Workflow.Folder!.Path, e.CancelledBy,
                e.Status == ExecutionStatus.Failed
                    ? e.Steps
                        .Where(s => s.Status == ExecutionStatus.Failed && s.TargetMachine != null)
                        .OrderByDescending(s => s.CompletedAt)
                        .Select(s => s.TargetMachine)
                        .FirstOrDefault()
                    : null))
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;
        row = (await ExecutionEventSupport.ResolveTargetMachineNamesAsync(db, [row], ct))[0];
        return eventType == NotificationEventType.CredentialFailure
            ? ExecutionEventSupport.BuildCredentialFailureContext(row)
            : ExecutionEventSupport.BuildContext(row);
    }
}
