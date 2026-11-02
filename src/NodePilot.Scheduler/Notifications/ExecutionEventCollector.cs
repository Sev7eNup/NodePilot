using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Notifications;

namespace NodePilot.Scheduler.Notifications;

/// <summary>
/// Terminal-execution collector: scans individually uncollected completed executions, builds one
/// context per row — plus a synthetic <see cref="NotificationEventType.CredentialFailure"/>
/// context when a failure smells credential-shaped and any rule wants that event — and
/// stages their collection flags so the pipeline's SaveChanges persists them together with the
/// Pending attempts. Completion timestamps do not determine whether a late commit is eligible.
/// EventKey shape: <c>exec:{guidN}:{EventTypeName}</c>.
/// </summary>
internal sealed class ExecutionEventCollector : INotificationCollector
{
    public async Task<NotificationCollection?> CollectAsync(
        NodePilotDbContext db, IReadOnlyList<NotificationRule> enabledRules, DateTime now, CancellationToken ct)
    {
        var state = await db.NotificationDispatcherStates.FirstOrDefaultAsync(s => s.Id == NotificationDispatcherState.SingletonId, ct);
        if (state is null)
        {
            // Suppress only history visible during initialization. A completion that commits
            // afterward stays uncollected, even if its timestamp predates initialization.
            state = new NotificationDispatcherState { Id = NotificationDispatcherState.SingletonId, InitializedAt = now, UpdatedAt = now };
            db.NotificationDispatcherStates.Add(state);
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.WorkflowExecutions.Where(e => e.CompletedAt != null
                    && (e.Status == ExecutionStatus.Succeeded || e.Status == ExecutionStatus.Failed || e.Status == ExecutionStatus.Cancelled))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.TerminalNotificationCollected, true), ct);
                await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, ct);
                await transaction.CommitAsync(ct);
                db.ChangeTracker.AcceptAllChanges();
            });
            return null;
        }

        var batch = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => !e.TerminalNotificationCollected && e.CompletedAt != null
                && (e.Status == ExecutionStatus.Succeeded || e.Status == ExecutionStatus.Failed || e.Status == ExecutionStatus.Cancelled))
            .OrderBy(e => e.CompletedAt).ThenBy(e => e.Id)
            .Take(ExecutionEventSupport.ScanBatchSize)
            .Select(ExecutionEventSupport.ProjectionWithFailedStepMachine)
            .ToListAsync(ct);

        if (batch.Count == 0) return null;
        batch = await ExecutionEventSupport.ResolveTargetMachineNamesAsync(db, batch, ct);

        var contexts = batch.Select(ExecutionEventSupport.BuildContext).ToList<NotificationContext>();
        if (enabledRules.Any(r => NotificationRuleSemantics.RuleWants(r, NotificationEventType.CredentialFailure)))
            contexts.AddRange(batch.Where(ExecutionEventSupport.LooksLikeCredentialFailure)
                .Select(ExecutionEventSupport.BuildCredentialFailureContext));

        // Track only the collection property; no execution payload needs loading. The pipeline
        // saves it with Pending attempts even when all rules filter or suppress the event.
        foreach (var row in batch)
        {
            var execution = db.WorkflowExecutions.Local.FirstOrDefault(e => e.Id == row.Id)
                ?? db.WorkflowExecutions.Attach(new WorkflowExecution { Id = row.Id }).Entity;
            execution.TerminalNotificationCollected = true;
        }
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
            .Select(ExecutionEventSupport.ProjectionWithFailedStepMachine)
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;
        row = (await ExecutionEventSupport.ResolveTargetMachineNamesAsync(db, [row], ct))[0];
        return eventType == NotificationEventType.CredentialFailure
            ? ExecutionEventSupport.BuildCredentialFailureContext(row)
            : ExecutionEventSupport.BuildContext(row);
    }
}
