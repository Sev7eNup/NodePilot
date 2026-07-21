using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Data;

namespace NodePilot.Api.Security;

/// <summary>
/// Leader-only fail-closed enforcement for already-running work. Request middleware and
/// dispatch admission reject a stale external principal, but neither can stop an execution
/// that already owns an engine worker when its directory becomes unavailable. This sweep
/// revokes sessions and cancels active work before the configured freshness deadline.
/// </summary>
public sealed class ExternalAuthorizationStalenessService(
    IServiceScopeFactory scopeFactory,
    IOptions<AuthenticationPolicyOptions> policy,
    IClusterStateProvider cluster,
    ILogger<ExternalAuthorizationStalenessService> logger) : BackgroundService
{
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan DeadlineSafetyMargin = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (cluster.IsLeader)
            {
                try
                {
                    await SweepOnceAsync(DateTime.UtcNow, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "External authorization staleness sweep failed");
                }
            }

            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task<int> SweepOnceAsync(DateTime now, CancellationToken ct)
    {
        var expectedLeaseEpoch = cluster.LeaseEpoch;
        var expectedLeaderNodeId = cluster.NodeId;
        if (!cluster.IsLeader) return 0;
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<NodePilotDbContext>();
        var cache = services.GetRequiredService<IMemoryCache>();
        var audit = services.GetRequiredService<IAuditWriter>();
        var evaluator = services.GetRequiredService<ExternalAuthorizationEvaluator>();
        var maxMinutes = Math.Clamp(policy.Value.MaxAuthorizationStalenessMinutes, 1, 15);
        var sessionUsers = db.AuthSessions.AsNoTracking()
            .Where(session => session.RevokedAt == null && session.ExpiresAt > now)
            .Select(session => session.UserId);
        var executionUsers = db.WorkflowExecutions.AsNoTracking()
            .Where(execution => execution.StartedByUserId != null
                             && (execution.Status == ExecutionStatus.Pending
                                 || execution.Status == ExecutionStatus.Running
                                 || execution.Status == ExecutionStatus.Paused))
            .Select(execution => execution.StartedByUserId!.Value);
        var candidateUserIds = await sessionUsers.Union(executionUsers).ToListAsync(ct);
        if (candidateUserIds.Count == 0) return 0;

        var externalUsers = await db.Users
            .Where(user => candidateUserIds.Contains(user.Id)
                        && user.Provider != AuthProvider.Local)
            .ToListAsync(ct);
        var activeExternalUsers = externalUsers
            .Where(user => user.IsActive && !user.IsTombstoned)
            .ToList();
        var evaluations = await evaluator.EvaluateManyAsync(
            activeExternalUsers, now + DeadlineSafetyMargin, ct);
        var staleUsers = activeExternalUsers
            .Where(user => !evaluations[user.Id].IsCurrent)
            .ToList();
        var invalidUsers = externalUsers
            .Where(user => !user.IsActive || user.IsTombstoned)
            .Concat(staleUsers)
            .DistinctBy(user => user.Id)
            .ToList();
        if (invalidUsers.Count == 0) return 0;
        if (!HasExpectedLease(expectedLeaseEpoch))
            return 0;

        var userIds = invalidUsers.Select(user => user.Id).ToList();
        var newlyStale = staleUsers.Where(user => user.DirectorySyncStatus != "Stale").ToList();
        var newlyStaleIds = newlyStale.Select(user => user.Id).ToHashSet();
        var strategy = db.Database.CreateExecutionStrategy();
        var mutation = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            if (!HasExpectedLease(expectedLeaseEpoch))
                return (Committed: false,
                    EngineOwned: (IReadOnlyList<Guid>)[],
                    SessionCounts: new Dictionary<Guid, int>(),
                    ExecutionCounts: new Dictionary<Guid, int>());

            var usersToMarkStale = await db.Users
                .Where(user => newlyStaleIds.Contains(user.Id))
                .ToListAsync(ct);
            foreach (var user in usersToMarkStale.Where(user => user.DirectorySyncStatus != "Stale"))
            {
                user.DirectorySyncStatus = "Stale";
                UserSessionInvalidation.BumpSecurityStamp(user);
            }

            var sessions = await db.AuthSessions
                .Where(session => userIds.Contains(session.UserId) && session.RevokedAt == null)
                .ToListAsync(ct);
            foreach (var session in sessions) session.RevokedAt = now;
            var sessionCounts = sessions.GroupBy(session => session.UserId)
                .ToDictionary(group => group.Key, group => group.Count());
            var executionCounts = await db.WorkflowExecutions.AsNoTracking()
                .Where(execution => execution.StartedByUserId != null
                                 && userIds.Contains(execution.StartedByUserId.Value)
                                 && (execution.Status == ExecutionStatus.Pending
                                     || execution.Status == ExecutionStatus.Running
                                     || execution.Status == ExecutionStatus.Paused))
                .GroupBy(execution => execution.StartedByUserId!.Value)
                .ToDictionaryAsync(group => group.Key, group => group.Count(), ct);

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (expectedLeaseEpoch > 0)
            {
                // Validate and lock the lease row in the same transaction as user/session/
                // execution offboarding. SET column=column is intentionally a no-op value-wise,
                // but obtains the provider's update lock until commit, so a handoff cannot pass
                // between this check and the security mutation.
                var fenced = await db.ClusterLeaders
                    .Where(leader => leader.Resource == "primary"
                                  && leader.OwnerNodeId == expectedLeaderNodeId
                                  && leader.LeaseEpoch == expectedLeaseEpoch
                                  && leader.ExpiresAt > DateTime.UtcNow)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(leader => leader.LastRenewedAt, leader => leader.LastRenewedAt), ct);
                if (fenced != 1)
                {
                    await transaction.RollbackAsync(ct);
                    db.ChangeTracker.Clear();
                    return (Committed: false,
                        EngineOwned: (IReadOnlyList<Guid>)[],
                        SessionCounts: new Dictionary<Guid, int>(),
                        ExecutionCounts: new Dictionary<Guid, int>());
                }
            }

            var engineOwned = await ExternalExecutionCancellation.CancelAsync(
                db,
                userIds,
                now,
                "authorization-stale",
                "Execution cancelled because its external authorization snapshot expired.",
                ct,
                expectedLeaderNodeId: expectedLeaderNodeId,
                expectedLeaseEpoch: expectedLeaseEpoch);
            if (!HasExpectedLease(expectedLeaseEpoch))
            {
                await transaction.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                return (Committed: false,
                    EngineOwned: (IReadOnlyList<Guid>)[],
                    SessionCounts: new Dictionary<Guid, int>(),
                    ExecutionCounts: new Dictionary<Guid, int>());
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return (Committed: true,
                EngineOwned: engineOwned,
                SessionCounts: sessionCounts,
                ExecutionCounts: executionCounts);
        });
        if (!mutation.Committed) return 0;

        foreach (var userId in userIds)
            UserSessionInvalidation.InvalidateUserStateCache(cache, userId);

        var engine = services.GetService<IWorkflowEngine>();
        if (engine is null && mutation.EngineOwned.Count > 0)
        {
            logger.LogError(
                "Cannot signal {Count} locally running executions for stale external principals because IWorkflowEngine is unavailable; durable cancellation is committed",
                mutation.EngineOwned.Count);
        }
        else if (mutation.EngineOwned.Count > 0)
        {
            // The durable Cancelled state is committed first. Do not inherit the host sweep
            // token for this short in-memory wake-up; a shutdown immediately after commit
            // must not suppress the signal to an engine that is still winding down.
            using var signalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await ExternalExecutionCancellation.SignalAfterCommitAsync(
                    engine,
                    mutation.EngineOwned,
                    "authorization-stale",
                    signalTimeout.Token,
                    logger);
            }
            catch (OperationCanceledException) when (signalTimeout.IsCancellationRequested)
            {
                logger.LogError(
                    "Post-commit authorization-staleness execution signal timed out for {Count} execution(s)",
                    mutation.EngineOwned.Count);
            }
        }

        foreach (var user in newlyStale)
        {
            if (!HasExpectedLease(expectedLeaseEpoch))
                return invalidUsers.Count;
            await audit.LogAsync(
                AuditActions.UserAuthorizationStale,
                "User",
                user.Id,
                AuditDetails.Json(
                    ("lastDirectorySyncAt", user.LastDirectorySyncAt?.ToString("O")),
                    ("maxStalenessMinutes", maxMinutes),
                    ("sessionsRevoked", mutation.SessionCounts.GetValueOrDefault(user.Id)),
                    ("executionsCancelled", mutation.ExecutionCounts.GetValueOrDefault(user.Id))),
                ct);
        }

        return invalidUsers.Count;
    }

    private bool HasExpectedLease(long expectedLeaseEpoch) =>
        cluster.IsLeader && cluster.LeaseEpoch == expectedLeaseEpoch;

}
