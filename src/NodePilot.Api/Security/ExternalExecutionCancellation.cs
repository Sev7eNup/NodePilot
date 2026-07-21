using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Api.Security;

/// <summary>
/// Atomically makes queued and engine-owned work terminal when an external principal
/// loses authorization. This helper performs durable writes only. Callers must commit
/// their surrounding identity transaction and then invoke <see cref="SignalAfterCommitAsync"/>;
/// an in-memory signal before commit could kill valid work when that transaction rolls back.
/// </summary>
internal static class ExternalExecutionCancellation
{
    public static async Task<IReadOnlyList<Guid>> CancelAsync(
        NodePilotDbContext db,
        IReadOnlyCollection<Guid> userIds,
        DateTime now,
        string reason,
        string message,
        CancellationToken ct,
        string? expectedLeaderNodeId = null,
        long expectedLeaseEpoch = 0)
    {
        if (userIds.Count == 0) return [];

        var executionIds = new List<Guid>();
        foreach (var userIdBatch in userIds.Chunk(500))
        {
            executionIds.AddRange(await db.WorkflowExecutions
                .AsNoTracking()
                .Where(execution => execution.StartedByUserId != null
                                 && userIdBatch.Contains(execution.StartedByUserId.Value)
                                 && (execution.Status == ExecutionStatus.Pending
                                     || execution.Status == ExecutionStatus.Running
                                     || execution.Status == ExecutionStatus.Paused))
                .Select(execution => execution.Id)
                .ToListAsync(ct));
        }
        if (executionIds.Count == 0) return executionIds;

        // ExecuteUpdate bypasses the change tracker. Detach any stale Pending/Running
        // instances so a later SaveChanges in the surrounding identity transaction cannot
        // overwrite the terminal state written here.
        foreach (var entry in db.ChangeTracker.Entries<WorkflowExecution>()
                     .Where(entry => executionIds.Contains(entry.Entity.Id)))
            entry.State = EntityState.Detached;

        var cancelledExecutionIds = new List<Guid>(executionIds.Count);
        foreach (var executionIdBatch in executionIds.Chunk(500))
        {
            var candidates = db.WorkflowExecutions
                .Where(execution => executionIdBatch.Contains(execution.Id)
                                 && (execution.Status == ExecutionStatus.Pending
                                     || execution.Status == ExecutionStatus.Running
                                     || execution.Status == ExecutionStatus.Paused));
            if (expectedLeaseEpoch > 0 && expectedLeaderNodeId is not null)
            {
                candidates = candidates.Where(_ => db.ClusterLeaders.Any(leader =>
                    leader.Resource == "primary"
                    && leader.OwnerNodeId == expectedLeaderNodeId
                    && leader.LeaseEpoch == expectedLeaseEpoch
                    && leader.ExpiresAt > DateTime.UtcNow));
            }
            await candidates
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(execution => execution.Status, ExecutionStatus.Cancelled)
                    .SetProperty(execution => execution.CancelledBy, reason)
                    .SetProperty(execution => execution.CompletedAt, now)
                    .SetProperty(execution => execution.ErrorMessage, message), ct);

            cancelledExecutionIds.AddRange(await db.WorkflowExecutions
                .AsNoTracking()
                .Where(execution => executionIdBatch.Contains(execution.Id)
                                 && execution.Status == ExecutionStatus.Cancelled
                                 && execution.CancelledBy == reason
                                 && execution.CompletedAt == now
                                 && execution.ErrorMessage == message)
                .Select(execution => execution.Id)
                .ToListAsync(ct));
        }

        return cancelledExecutionIds;
    }

    /// <summary>
    /// Signals in-memory engine ownership only after the caller has committed the durable
    /// terminal execution rows. A second pass covers a token registration racing the first
    /// lookup; a later Pending claim cannot succeed because the committed row is terminal.
    /// </summary>
    public static async Task SignalAfterCommitAsync(
        IWorkflowEngine? engine,
        IReadOnlyCollection<Guid> executionIds,
        string reason,
        CancellationToken ct,
        ILogger? logger = null)
    {
        var missed = await SignalBestEffortAsync(
            engine, executionIds, reason, ct, logger);
        await SignalBestEffortAsync(engine, missed, reason, ct, logger);
    }

    private static async Task<IReadOnlyList<Guid>> SignalBestEffortAsync(
        IWorkflowEngine? engine,
        IReadOnlyCollection<Guid> executionIds,
        string reason,
        CancellationToken ct,
        ILogger? logger)
    {
        if (engine is null || executionIds.Count == 0) return [];
        var missed = new List<Guid>();
        foreach (var executionId in executionIds)
        {
            try
            {
                if (!await engine.CancelAsync(executionId, reason, ct))
                    missed.Add(executionId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The durable row is already terminal. Keep the callback outcome stable,
                // retry once below and leave a high-signal operational event if the local
                // engine token could not be reached.
                logger?.LogError(ex,
                    "Post-commit execution cancellation signal failed for {ExecutionId}",
                    executionId);
                missed.Add(executionId);
            }
        }
        return missed;
    }

}
