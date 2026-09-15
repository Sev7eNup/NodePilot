using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Availability;

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
        string? ourNodeId = null, long? leaseEpoch = null, TimeSpan? clusterBatchTimeout = null,
        IDatabaseAvailability? availability = null)
    {
        var now = DateTime.UtcNow;

        if (ourNodeId is not null && leaseEpoch is { } expectedEpoch)
        {
            return await RecoverClusterOrphansAsync(
                db, logger, ourNodeId, expectedEpoch, clusterBatchTimeout ?? TimeSpan.FromSeconds(2), availability, ct);
        }

        // Cluster mode recovers rows whose owner is not us (NULL counts as not-us, so rows
        // with no owner recover too). Single-node mode recovers everything non-terminal.
        // Bounded batches keep a large backlog out of memory: each batch re-queries the same
        // predicate, so rows already flipped to Cancelled fall out of the next page on their
        // own. Batches commit independently, so a crash mid-recovery leaves partial progress
        // that the next startup run picks up.
        const int batchSize = 500;
        var totalExecutions = 0;
        var totalSteps = 0;
        var totalReleasedReservations = 0;

        // Pending rows with an outbox intent are accepted work, not orphans. Release any lease
        // left by the dead process; the durable dispatcher will claim them after startup.
        await db.ExecutionDispatchOutbox.ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.LeaseOwner, (string?)null)
            .SetProperty(item => item.LeaseExpiresAt, (DateTime?)null), ct);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var batchQuery = db.WorkflowExecutions
                .Where(e => e.Status == ExecutionStatus.Running
                         || e.Status == ExecutionStatus.Paused
                         || (e.Status == ExecutionStatus.Pending
                             && !db.ExecutionDispatchOutbox.Any(item => item.ExecutionId == e.Id)));
            if (ourNodeId is not null)
                batchQuery = batchQuery.Where(e => e.OwnerNodeId != ourNodeId);

            var batch = await batchQuery
                .OrderBy(e => e.Id)
                .Take(batchSize)
                .ToListAsync(ct);
            if (batch.Count == 0)
                break;

            var neverStartedIds = batch
                .Where(execution => execution.Status == ExecutionStatus.Pending)
                .Select(execution => execution.Id)
                .ToHashSet();

            foreach (var e in batch)
            {
                var wasPending = e.Status == ExecutionStatus.Pending;
                var wasPaused = e.Status == ExecutionStatus.Paused;
                e.Status = ExecutionStatus.Cancelled;
                e.CancelledBy = wasPending
                    ? ourNodeId is not null ? "failover-pending" : "reconciler-pending"
                    : ourNodeId is not null ? "failover" : "reconciler";
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

            // A Pending Execution has not crossed engine ownership, so releasing its external
            // idempotency reservation is safe and lets the caller retry after this restart. Keep
            // reservations for Running/Paused executions: their external side effects are
            // ambiguous and replaying automatically could duplicate them.
            List<IdempotencyKey> releasedReservations = neverStartedIds.Count == 0
                ? []
                : await db.IdempotencyKeys
                    .Where(key => neverStartedIds.Contains(key.ExecutionId))
                    .ToListAsync(ct);
            db.IdempotencyKeys.RemoveRange(releasedReservations);

            await db.SaveChangesAsync(ct);
            // Detach the batch before the next round so the change tracker never accumulates
            // the whole backlog across iterations.
            db.ChangeTracker.Clear();

            totalExecutions += batch.Count;
            totalSteps += stepOrphans.Count;
            totalReleasedReservations += releasedReservations.Count;

            // A short batch means the backlog is drained — skip the extra empty round-trip.
            if (batch.Count < batchSize)
                break;
        }

        if (totalExecutions == 0)
            return 0;

        logger.LogWarning(
            "Startup recovery: marked {ExecutionCount} orphaned execution(s) and {StepCount} orphaned step(s) as Cancelled; released {ReservationCount} never-started idempotency reservation(s). Mode={Mode}",
            totalExecutions, totalSteps, totalReleasedReservations,
            ourNodeId is not null ? $"cluster-failover ourNodeId={ourNodeId}" : "single-node");
        return totalExecutions;
    }

    private static async Task<int> RecoverClusterOrphansAsync(
        NodePilotDbContext db,
        ILogger logger,
        string ourNodeId,
        long leaseEpoch,
        TimeSpan batchTimeout,
        IDatabaseAvailability? availability,
        CancellationToken ct)
    {
        if (batchTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(batchTimeout));
        var batchSize = 100;
        var totalExecutions = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            db.ChangeTracker.Clear();
            // Select a bounded page before locking the lease. Processed rows leave this predicate.
            var ids = await db.WorkflowExecutions.AsNoTracking()
                .Where(e => e.OwnerNodeId != ourNodeId
                         && (e.Status == ExecutionStatus.Pending || e.Status == ExecutionStatus.Running
                             || e.Status == ExecutionStatus.Paused))
                .OrderBy(e => e.Id)
                .Select(e => e.Id)
                .Take(batchSize)
                .ToArrayAsync(ct);
            if (ids.Length == 0) return totalExecutions;

            var batch = new RecoveryBatch(ids);
            RecoveryBatchResult result;
            try
            {
                result = await RecoverClusterBatchAsync(db, logger, batch, ourNodeId, leaseEpoch, batchTimeout, availability, ct);
            }
            catch (RecoveryBatchTimeoutException ex)
            {
                db.ChangeTracker.Clear();
                if (ids.Length == 1)
                    throw new ClusterRecoveryDeferredException(totalExecutions, ex);
                batchSize = Math.Max(1, Math.Min(batchSize, ids.Length) / 2);
                logger.LogWarning(ex,
                    "Cluster recovery batch exceeded its budget; retrying with {BatchSize} executions. Epoch={Epoch}",
                    batchSize, leaseEpoch);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                continue;
            }

            db.ChangeTracker.Clear();
            if (result.LeaseLost)
            {
                logger.LogWarning("Cluster recovery fenced: node {NodeId} no longer owns epoch {Epoch}",
                    ourNodeId, leaseEpoch);
                return totalExecutions;
            }
            totalExecutions += result.Audits.Count;
            foreach (var entry in result.Audits) AuditEventForwarder.ForwardCommitted(logger, entry);
            logger.LogInformation(
                "Cluster recovery batch committed: {ExecutionCount} cancelled, {PendingCount} adopted, {StepCount} steps, {ReservationCount} reservations. Epoch={Epoch}",
                result.Audits.Count, result.AdoptedIds.Length, result.Steps, result.Reservations, leaseEpoch);
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }
    }

    private static async Task<RecoveryBatchResult> RecoverClusterBatchAsync(
        NodePilotDbContext db,
        ILogger logger,
        RecoveryBatch batch,
        string ourNodeId,
        long leaseEpoch,
        TimeSpan batchTimeout,
        IDatabaseAvailability? availability,
        CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async _ =>
        {
            ct.ThrowIfCancellationRequested();
            db.ChangeTracker.Clear();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(batchTimeout);
            var token = deadline.Token;
            var commitAttempted = false;
            RecoveryBatchResult? result = null;
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                var leaseLocked = await db.ClusterLeaders
                    .Where(leader => leader.Resource == "primary"
                                  && leader.OwnerNodeId == ourNodeId
                                  && leader.LeaseEpoch == leaseEpoch
                                  && leader.ExpiresAt > DateTime.UtcNow)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(leader => leader.LastRenewedAt, leader => leader.LastRenewedAt), token);
                if (leaseLocked != 1) return new RecoveryBatchResult(true, [], [], 0, 0);

                result = await ApplyClusterBatchAsync(db, batch, ourNodeId, leaseEpoch, token);
                commitAttempted = true;
                await transaction.CommitAsync(token);
                return result;
            }
            catch (Exception ex)
            {
                // The transaction has been disposed before verification or any retry/backoff.
                db.ChangeTracker.Clear();
                if (commitAttempted && result is not null && !ct.IsCancellationRequested)
                {
                    if (await ReconcileClusterCommitAsync(db, logger, result, ourNodeId,
                        batchTimeout, availability, ct)) return result;
                }
                if (!ct.IsCancellationRequested && (deadline.IsCancellationRequested
                    || DbErrorClassifier.Classify(ex) == DbFailureKind.CommandTimeout))
                    throw new RecoveryBatchTimeoutException(ex);
                throw;
            }
        }, ct);
    }

    private static async Task<bool> ReconcileClusterCommitAsync(
        NodePilotDbContext db, ILogger logger, RecoveryBatchResult result, string ourNodeId,
        TimeSpan batchTimeout, IDatabaseAvailability? availability, CancellationToken ct)
    {
        // Keep the exact batch result until the commit is resolved. These retries hold no
        // transaction or lease lock and cannot replay writes after a failed verification read.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (availability is not null && !await availability.WaitUntilServableAsync(ct))
                ct.ThrowIfCancellationRequested();
            using var verification = CancellationTokenSource.CreateLinkedTokenSource(ct);
            verification.CancelAfter(batchTimeout);
            try
            {
                return await VerifyClusterBatchAsync(db, result, ourNodeId, verification.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                db.ChangeTracker.Clear();
                logger.LogWarning(ex, "Cluster recovery commit verification failed; retaining the batch result and retrying the read");
                await Task.Delay(verification.IsCancellationRequested
                    ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private static async Task<RecoveryBatchResult> ApplyClusterBatchAsync(
        NodePilotDbContext db, RecoveryBatch batch, string ourNodeId, long leaseEpoch, CancellationToken ct)
    {
        var candidates = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => batch.Ids.Contains(e.Id) && e.OwnerNodeId != ourNodeId
                     && (e.Status == ExecutionStatus.Pending || e.Status == ExecutionStatus.Running
                         || e.Status == ExecutionStatus.Paused))
            .Select(e => new { e.Id, e.OwnerNodeId })
            .ToListAsync(ct);
        var ids = candidates.Select(e => e.Id).ToArray();
        if (ids.Length == 0) return new RecoveryBatchResult(false, [], [], 0, 0);

        await db.WorkflowExecutions
            .Where(e => ids.Contains(e.Id) && e.OwnerNodeId != ourNodeId && e.Status == ExecutionStatus.Pending
                     && db.ExecutionDispatchOutbox.Any(item => item.ExecutionId == e.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.OwnerNodeId, ourNodeId), ct);
        var adoptedIds = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => ids.Contains(e.Id) && e.OwnerNodeId == ourNodeId && e.Status == ExecutionStatus.Pending)
            .Select(e => e.Id).ToArrayAsync(ct);

        await db.WorkflowExecutions
            .Where(e => ids.Contains(e.Id) && e.OwnerNodeId != ourNodeId
                     && (e.Status == ExecutionStatus.Running || e.Status == ExecutionStatus.Paused
                         || (e.Status == ExecutionStatus.Pending
                             && !db.ExecutionDispatchOutbox.Any(item => item.ExecutionId == e.Id))))
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, ExecutionStatus.Cancelled)
                .SetProperty(e => e.CancelledBy, e => e.Status == ExecutionStatus.Pending ? "failover-pending" : "failover")
                .SetProperty(e => e.CompletedAt, batch.CompletedAt)
                .SetProperty(e => e.ErrorMessage, e => "Cluster failover recovery — original owner '"
                    + (e.OwnerNodeId ?? "<null>") + $"', recovered by '{ourNodeId}', leaseEpoch={leaseEpoch}."), ct);
        var recovered = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => ids.Contains(e.Id) && e.Status == ExecutionStatus.Cancelled
                     && e.CompletedAt == batch.CompletedAt
                     && (e.CancelledBy == "failover" || e.CancelledBy == "failover-pending"))
            .Select(e => new { e.Id, e.CancelledBy }).ToListAsync(ct);
        var recoveredIds = recovered.Select(e => e.Id).ToArray();
        var pendingIds = recovered.Where(e => e.CancelledBy == "failover-pending").Select(e => e.Id).ToArray();

        if (adoptedIds.Length > 0)
            await db.ExecutionDispatchOutbox.Where(item => adoptedIds.Contains(item.ExecutionId))
                .ExecuteUpdateAsync(s => s.SetProperty(item => item.LeaseOwner, (string?)null)
                    .SetProperty(item => item.LeaseExpiresAt, (DateTime?)null), ct);
        var steps = recoveredIds.Length == 0 ? 0 : await ExecutionStateLifecycle.CancelOrphanedStepsAsync(
            db.StepExecutions, recoveredIds, batch.CompletedAt,
            $"Step orphaned by cluster failover (recovered by '{ourNodeId}').", ct);
        var reservations = pendingIds.Length == 0 ? 0 : await db.IdempotencyKeys
            .Where(key => pendingIds.Contains(key.ExecutionId)).ExecuteDeleteAsync(ct);

        var owners = candidates.ToDictionary(e => e.Id, e => e.OwnerNodeId);
        var stager = new AuditStager();
        var audits = recoveredIds.Select(id =>
        {
            var entry = stager.Build(AuditActions.ExecutionRecoveredFailover, AuditActor.System,
                "WorkflowExecution", id, AuditDetails.Json(("originalOwnerNodeId", owners[id]),
                    ("recoveredByNodeId", ourNodeId), ("leaseEpoch", leaseEpoch)));
            entry.Id = batch.AuditIds[id];
            entry.Timestamp = batch.CompletedAt;
            return entry;
        }).ToArray();
        db.AuditLog.AddRange(audits);
        if (audits.Length > 0) await db.SaveChangesAsync(ct);
        return new RecoveryBatchResult(false, audits, adoptedIds, steps, reservations);
    }

    private static async Task<bool> VerifyClusterBatchAsync(
        NodePilotDbContext db, RecoveryBatchResult result, string ourNodeId, CancellationToken ct)
    {
        // Stable audit identities prove the entire atomic batch committed, including adoption.
        if (result.Audits.Count > 0)
        {
            var auditIds = result.Audits.Select(a => a.Id).ToArray();
            return await db.AuditLog.AsNoTracking().CountAsync(a => auditIds.Contains(a.Id), ct) == auditIds.Length;
        }
        return result.AdoptedIds.Length == 0 || await db.WorkflowExecutions.AsNoTracking()
            .CountAsync(e => result.AdoptedIds.Contains(e.Id) && e.OwnerNodeId == ourNodeId, ct)
                == result.AdoptedIds.Length;
    }

    private sealed class RecoveryBatch(Guid[] ids)
    {
        public Guid[] Ids { get; } = ids;
        public DateTime CompletedAt { get; } = DateTime.UtcNow;
        public Dictionary<Guid, Guid> AuditIds { get; } = ids.ToDictionary(id => id, _ => Guid.NewGuid());
    }

    private sealed record RecoveryBatchResult(
        bool LeaseLost, IReadOnlyList<AuditLogEntry> Audits, Guid[] AdoptedIds, int Steps, int Reservations);

    private sealed class RecoveryBatchTimeoutException(Exception innerException)
        : Exception("Cluster recovery exceeded its transaction budget.", innerException);
}

public sealed class ClusterRecoveryDeferredException(int completedExecutions, Exception innerException)
    : Exception("A single execution exceeded the cluster recovery transaction budget; recovery must continue later.", innerException)
{
    public int CompletedExecutions { get; } = completedExecutions;
}
