using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Availability;

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
    ILogger<ExternalAuthorizationStalenessService> logger,
    IDatabaseAvailability availability) : BackgroundService
{
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan DeadlineSafetyMargin = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Availability gate, deliberately ABOVE the leader check: during a database outage no
            // node can renew its cluster lease, so every node reads as a follower - gating on
            // IsLeader first would park for the right reason and log the wrong one.
            if (!await availability.WaitUntilServableAsync(stoppingToken)) break;

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

        var externalUserIds = await db.Users.AsNoTracking()
            .Where(user => candidateUserIds.Contains(user.Id)
                        && user.Provider != AuthProvider.Local)
            .Select(user => user.Id)
            .ToListAsync(ct);
        if (externalUserIds.Count == 0) return 0;
        if (!HasExpectedLease(expectedLeaseEpoch))
            return 0;

        var strategy = db.Database.CreateExecutionStrategy();
        var mutation = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            if (!HasExpectedLease(expectedLeaseEpoch))
                return SweepMutation.None;

            // Freshness and its resulting revocations belong to the same serializable
            // attempt (ADR 0009/0014). A successful concurrent sync must not be overwritten
            // by a verdict computed before it, even when it leaves SecurityStamp unchanged.
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
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
                    return SweepMutation.None;
                }
            }

            var externalUsers = await db.Users
                .Where(user => externalUserIds.Contains(user.Id) && user.Provider != AuthProvider.Local)
                .ToListAsync(ct);
            var activeExternalUsers = externalUsers.Where(user => user.IsActive && !user.IsTombstoned).ToList();
            var evaluations = await evaluator.EvaluateManyAsync(activeExternalUsers, now + DeadlineSafetyMargin, ct);
            var staleUsers = activeExternalUsers.Where(user => !evaluations[user.Id].IsCurrent).ToList();
            var invalidUsers = externalUsers.Where(user => !user.IsActive || user.IsTombstoned)
                .Concat(staleUsers).DistinctBy(user => user.Id).ToList();
            if (invalidUsers.Count == 0)
                return SweepMutation.None;
            var userIds = invalidUsers.Select(user => user.Id).ToList();
            var newlyStale = staleUsers.Where(user => user.DirectorySyncStatus != "Stale").ToList();
            foreach (var user in newlyStale)
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
                return SweepMutation.None;
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new SweepMutation(userIds, newlyStale, engineOwned, sessionCounts, executionCounts);
        });
        if (mutation.UserIds.Count == 0) return 0;

        foreach (var userId in mutation.UserIds)
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

        foreach (var user in mutation.NewlyStale)
        {
            if (!HasExpectedLease(expectedLeaseEpoch))
                return mutation.UserIds.Count;
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

        return mutation.UserIds.Count;
    }

    private sealed record SweepMutation(
        IReadOnlyList<Guid> UserIds,
        IReadOnlyList<User> NewlyStale,
        IReadOnlyList<Guid> EngineOwned,
        IReadOnlyDictionary<Guid, int> SessionCounts,
        IReadOnlyDictionary<Guid, int> ExecutionCounts)
    {
        internal static readonly SweepMutation None = new([], [], [], new Dictionary<Guid, int>(), new Dictionary<Guid, int>());
    }

    private bool HasExpectedLease(long expectedLeaseEpoch) =>
        cluster.IsLeader && cluster.LeaseEpoch == expectedLeaseEpoch;

}
