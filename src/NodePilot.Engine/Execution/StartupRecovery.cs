using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Enums;
using NodePilot.Data;

namespace NodePilot.Engine.Execution;

public static class StartupRecovery
{
    /// <summary>
    /// Finds DB rows still marked <see cref="ExecutionStatus.Running"/>,
    /// <see cref="ExecutionStatus.Pending"/>, or <see cref="ExecutionStatus.Paused"/> at
    /// startup. Single-node call: ownerNodeId is null and we recover everything that's
    /// non-terminal. Cluster call: ownerNodeId is the new leader's id and we recover only
    /// rows whose <c>OwnerNodeId</c> doesn't match — this prevents a freshly-promoted
    /// leader from clobbering its own runs.
    /// <para>
    /// In cluster mode this method MUST NOT be called from the API boot path. Boot-time
    /// recovery there would let a starting follower (no leadership yet) abort the active
    /// leader's running rows. Cluster-aware callers wire it to
    /// <c>IClusterStateProvider.OnLeadershipAcquired</c> instead.
    /// </para>
    /// </summary>
    public static async Task<int> RecoverOrphanedExecutionsAsync(
        NodePilotDbContext db, ILogger logger, CancellationToken ct = default,
        string? ourNodeId = null, long? leaseEpoch = null)
    {
        var now = DateTime.UtcNow;

        if (ourNodeId is not null && leaseEpoch is { } expectedEpoch)
        {
            return await RecoverClusterOrphansAsync(
                db, logger, ourNodeId, expectedEpoch, ct);
        }

        // Cluster-mode predicate: only rows whose owner is NOT us. NULL counts as "not us"
        // (NULL != "anything" is true in EF/SQL), so legacy rows from before the cluster
        // feature shipped also get recovered the first time.
        // Single-node call (ourNodeId == null): recover everything non-terminal.
        // Process in bounded batches so a crash with a large in-flight backlog never
        // materializes every orphaned row (and its steps) into memory at once. Each batch
        // re-queries the same non-terminal predicate and flips its rows to Cancelled, so the
        // next Take(batchSize) naturally advances past the rows just recovered — no keyset
        // bookkeeping needed. Batches commit independently, so partial progress survives a
        // mid-recovery failure (startup recovery re-runs on the next boot regardless).
        const int batchSize = 500;
        var totalExecutions = 0;
        var totalSteps = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var batchQuery = db.WorkflowExecutions
                .Where(e => e.Status == ExecutionStatus.Running
                         || e.Status == ExecutionStatus.Pending
                         || e.Status == ExecutionStatus.Paused);
            if (ourNodeId is not null)
                batchQuery = batchQuery.Where(e => e.OwnerNodeId != ourNodeId);

            var batch = await batchQuery
                .OrderBy(e => e.Id)
                .Take(batchSize)
                .ToListAsync(ct);
            if (batch.Count == 0)
                break;

            foreach (var e in batch)
            {
                var wasPending = e.Status == ExecutionStatus.Pending;
                var wasPaused = e.Status == ExecutionStatus.Paused;
                e.Status = ExecutionStatus.Cancelled;
                e.CancelledBy = ourNodeId is not null ? "failover" : "reconciler";
                e.CompletedAt = now;
                e.ErrorMessage = ourNodeId is not null
                    ? $"Cluster failover recovery — original owner '{e.OwnerNodeId ?? "<null>"}', recovered by '{ourNodeId}', leaseEpoch={leaseEpoch?.ToString() ?? "?"}."
                    : wasPending
                        ? "Execution was queued but not dispatched before an API process restart and auto-cancelled on startup."
                        : wasPaused
                            ? "Paused execution lost in-memory debug state on process restart and auto-cancelled."
                            : "Execution was orphaned by an API process restart and auto-cancelled on startup.";
            }

            // Any Running step under this batch's executions is equally orphaned.
            var batchIds = batch.Select(e => e.Id).ToHashSet();
            var stepOrphans = await db.StepExecutions
                .Where(s => s.Status == ExecutionStatus.Running && batchIds.Contains(s.WorkflowExecutionId))
                .ToListAsync(ct);
            foreach (var s in stepOrphans)
            {
                s.Status = ExecutionStatus.Cancelled;
                s.CompletedAt = now;
                s.ErrorOutput = ourNodeId is not null
                    ? $"Step orphaned by cluster failover (recovered by '{ourNodeId}')."
                    : "Step orphaned by API restart.";
            }

            await db.SaveChangesAsync(ct);
            // Detach the batch before the next round so the change tracker never accumulates
            // the whole backlog across iterations.
            db.ChangeTracker.Clear();

            totalExecutions += batch.Count;
            totalSteps += stepOrphans.Count;

            // A short batch means the backlog is drained — skip the extra empty round-trip.
            if (batch.Count < batchSize)
                break;
        }

        if (totalExecutions == 0)
            return 0;

        logger.LogWarning(
            "Startup recovery: marked {ExecutionCount} orphaned execution(s) and {StepCount} orphaned step(s) as Cancelled. Mode={Mode}",
            totalExecutions, totalSteps,
            ourNodeId is not null ? $"cluster-failover ourNodeId={ourNodeId}" : "single-node");
        return totalExecutions;
    }

    private static async Task<int> RecoverClusterOrphansAsync(
        NodePilotDbContext db,
        ILogger logger,
        string ourNodeId,
        long leaseEpoch,
        CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            return await RecoverClusterOrphansAttemptAsync(
                db, logger, ourNodeId, leaseEpoch, DateTime.UtcNow, ct);
        });
    }

    private static async Task<int> RecoverClusterOrphansAttemptAsync(
        NodePilotDbContext db,
        ILogger logger,
        string ourNodeId,
        long leaseEpoch,
        DateTime now,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var leaseLocked = await db.ClusterLeaders
            .Where(leader => leader.Resource == "primary"
                          && leader.OwnerNodeId == ourNodeId
                          && leader.LeaseEpoch == leaseEpoch
                          && leader.ExpiresAt > DateTime.UtcNow)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(leader => leader.LastRenewedAt, leader => leader.LastRenewedAt), ct);
        if (leaseLocked != 1)
        {
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            logger.LogWarning(
                "Cluster failover recovery fenced: node {NodeId} no longer owns epoch {LeaseEpoch}",
                ourNodeId, leaseEpoch);
            return 0;
        }

        var candidateIds = await db.WorkflowExecutions.AsNoTracking()
            .Where(execution => (execution.Status == ExecutionStatus.Running
                                 || execution.Status == ExecutionStatus.Pending
                                 || execution.Status == ExecutionStatus.Paused)
                              && execution.OwnerNodeId != ourNodeId)
            .Select(execution => execution.Id)
            .ToListAsync(ct);
        if (candidateIds.Count == 0)
        {
            await transaction.CommitAsync(ct);
            return 0;
        }

        var recoveredIds = new List<Guid>(candidateIds.Count);
        foreach (var candidateIdBatch in candidateIds.Chunk(500))
        {
            await db.WorkflowExecutions
                .Where(execution => candidateIdBatch.Contains(execution.Id)
                                 && (execution.Status == ExecutionStatus.Running
                                     || execution.Status == ExecutionStatus.Pending
                                     || execution.Status == ExecutionStatus.Paused)
                                 && execution.OwnerNodeId != ourNodeId
                                 && db.ClusterLeaders.Any(leader =>
                                     leader.Resource == "primary"
                                     && leader.OwnerNodeId == ourNodeId
                                     && leader.LeaseEpoch == leaseEpoch
                                     && leader.ExpiresAt > DateTime.UtcNow))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(execution => execution.Status, ExecutionStatus.Cancelled)
                    .SetProperty(execution => execution.CancelledBy, "failover")
                    .SetProperty(execution => execution.CompletedAt, now)
                    .SetProperty(execution => execution.ErrorMessage, execution =>
                        "Cluster failover recovery — original owner '"
                        + (execution.OwnerNodeId ?? "<null>")
                        + $"', recovered by '{ourNodeId}', leaseEpoch={leaseEpoch}."), ct);

            recoveredIds.AddRange(await db.WorkflowExecutions.AsNoTracking()
                .Where(execution => candidateIdBatch.Contains(execution.Id)
                                 && execution.Status == ExecutionStatus.Cancelled
                                 && execution.CancelledBy == "failover"
                                 && execution.CompletedAt == now)
                .Select(execution => execution.Id)
                .ToListAsync(ct));
        }

        var recoveredSteps = 0;
        foreach (var recoveredIdBatch in recoveredIds.Chunk(500))
        {
            recoveredSteps += await db.StepExecutions
                .Where(step => recoveredIdBatch.Contains(step.WorkflowExecutionId)
                            && step.Status == ExecutionStatus.Running)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(step => step.Status, ExecutionStatus.Cancelled)
                    .SetProperty(step => step.CompletedAt, now)
                    .SetProperty(step => step.ErrorOutput,
                        $"Step orphaned by cluster failover (recovered by '{ourNodeId}')."), ct);
        }

        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
        logger.LogWarning(
            "Startup recovery: marked {ExecutionCount} orphaned execution(s) and {StepCount} orphaned step(s) as Cancelled. Mode={Mode}",
            recoveredIds.Count, recoveredSteps,
            $"cluster-failover ourNodeId={ourNodeId} leaseEpoch={leaseEpoch}");
        return recoveredIds.Count;
    }
}
