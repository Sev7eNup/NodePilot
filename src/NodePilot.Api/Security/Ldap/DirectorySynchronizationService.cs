using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Api.Security.Ldap;

/// <summary>
/// Leader-only reconciliation of AD account state, role mappings and group membership.
/// A failed directory lookup does not silently refresh freshness; after the configured
/// maximum staleness, TokenValidityMiddleware and workflow authorization fail closed.
/// </summary>
public sealed class DirectorySynchronizationService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<LdapOptions> options,
    IClusterStateProvider cluster,
    ILogger<DirectorySynchronizationService> logger,
    ActiveDirectoryAuthenticationConfiguration? activeConfiguration = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var cycleStartedAt = DateTime.UtcNow;
            var current = activeConfiguration?.Ldap ?? options.CurrentValue;
            var directorySyncEnabled = activeConfiguration?.DirectorySyncEnabled ?? current.Enabled;
            if (cluster.IsLeader && directorySyncEnabled)
            {
                try { await SyncOnceAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { logger.LogError(ex, "Directory synchronization pass failed"); }
            }

            var minutes = Math.Clamp(current.DirectorySyncIntervalMinutes, 1, 5);
            var remaining = TimeSpan.FromMinutes(minutes) - (DateTime.UtcNow - cycleStartedAt);
            if (remaining < TimeSpan.FromSeconds(1)) remaining = TimeSpan.FromSeconds(1);
            try { await Task.Delay(remaining, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task<int> SyncOnceAsync(CancellationToken ct)
    {
        var expectedLeaseEpoch = cluster.LeaseEpoch;
        if (!cluster.IsLeader) return 0;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        var adapter = scope.ServiceProvider.GetRequiredService<ILdapConnectionAdapter>();
        var auditStager = scope.ServiceProvider.GetRequiredService<IAuditStager>();
        var cache = scope.ServiceProvider.GetRequiredService<IMemoryCache>();
        var identityDescriptors = await db.ExternalIdentities.AsNoTracking()
            .Where(identity => identity.Authority == ExternalIdentity.ActiveDirectoryAuthority
                            && !identity.User.IsTombstoned)
            .Select(identity => new { identity.Id, identity.Subject })
            .ToListAsync(ct);
        var currentOptions = activeConfiguration?.Ldap ?? options.CurrentValue;
        var lookups = await adapter.LookupManyBySubjectAsync(
            identityDescriptors.Select(identity => identity.Subject).ToArray(),
            currentOptions.DirectorySyncMaxConcurrency,
            ct);

        // A valid-but-wrong BaseDn can make every search return zero rows. Never turn that
        // into a tenant-wide destructive pass; freshness still expires fail-closed.
        var completeAuthoritativePass = identityDescriptors.Count > 0
            && identityDescriptors.All(descriptor =>
                lookups.TryGetValue(descriptor.Subject, out var result) && result.Error is null);
        var rejectAllNotFoundPass = completeAuthoritativePass
            && identityDescriptors.All(descriptor => lookups[descriptor.Subject].Snapshot is null);
        if (rejectAllNotFoundPass)
        {
            logger.LogError(
                "Directory synchronization rejected an all-not-found pass for {Count} known AD identities. " +
                "Verify Authentication:Ldap:BaseDn and service-account search permissions.",
                identityDescriptors.Count);
        }

        var changedCount = 0;
        foreach (var descriptor in identityDescriptors)
        {
            ct.ThrowIfCancellationRequested();
            if (!HasExpectedLease(expectedLeaseEpoch)) return changedCount;
            lookups.TryGetValue(descriptor.Subject, out var lookup);
            var state = new SyncAttemptState(
                descriptor.Id,
                lookup,
                rejectAllNotFoundPass && lookup?.Snapshot is null,
                currentOptions,
                expectedLeaseEpoch,
                DateTime.UtcNow,
                Guid.NewGuid());

            SyncAttemptResult result;
            try
            {
                var strategy = db.Database.CreateExecutionStrategy();
                result = await strategy.ExecuteInTransactionAsync(
                    state,
                    (attempt, token) => ApplyAttemptAsync(db, auditStager, attempt, token),
                    (attempt, token) => VerifyAttemptSucceededAsync(db, attempt, token),
                    IsolationLevel.Serializable,
                    ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                logger.LogWarning(ex,
                    "Directory sync skipped identity {IdentityId} because its security state changed concurrently",
                    descriptor.Id);
                db.ChangeTracker.Clear();
                continue;
            }
            catch
            {
                db.ChangeTracker.Clear();
                throw;
            }

            if (result.LeaseLost) return changedCount;
            if (result.Skipped) continue;
            if (result.FailedLookup)
            {
                logger.LogWarning(lookup?.Error,
                    "Directory sync failed for user {UserId}", result.UserId);
                continue;
            }
            if (result.SecurityChanged)
            {
                changedCount++;
                UserSessionInvalidation.InvalidateUserStateCache(cache, result.UserId);
            }
            if (result.Audit is not null)
                AuditEventForwarder.ForwardCommitted(logger, result.Audit);
            await SignalEngineOwnedExecutionsAsync(
                scope.ServiceProvider, result.ExecutionIds);
        }

        return changedCount;
    }

    private async Task<SyncAttemptResult> ApplyAttemptAsync(
        NodePilotDbContext db,
        IAuditStager auditStager,
        SyncAttemptState state,
        CancellationToken ct)
    {
        // Every execution-strategy retry starts from database state, never entities whose
        // state was changed to Unchanged by a prior SaveChanges before a failed commit.
        db.ChangeTracker.Clear();
        if (!HasExpectedLease(state.ExpectedLeaseEpoch)
            || state.ExpectedLeaseEpoch > 0
            && !await AcquireDbLeaseFenceAsync(db, state.ExpectedLeaseEpoch, ct))
            return SyncAttemptResult.LeaseWasLost;

        var identity = await db.ExternalIdentities
            .Include(candidate => candidate.User)
            .SingleOrDefaultAsync(candidate => candidate.Id == state.IdentityId, ct);
        if (identity is null || identity.User.IsTombstoned)
            return SyncAttemptResult.Skip;
        var user = identity.User;

        if (state.Lookup is null || state.Lookup.Error is not null || state.RejectNotFound)
        {
            user.DirectorySyncStatus = "Failed";
            await db.SaveChangesAsync(ct);
            return new SyncAttemptResult(
                false, false, true, user.Id, false, [], null);
        }

        var snapshot = state.Lookup.Snapshot;
        var oldGroups = await db.DirectoryMemberships
            .Where(membership => membership.UserId == user.Id
                              && membership.Authority == ExternalIdentity.ActiveDirectoryAuthority)
            .ToListAsync(ct);
        var oldGroupKeys = oldGroups.Select(membership => membership.GroupKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desiredGroups = snapshot?.GroupSids
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? [];
        var allowedGroups = state.Options.AllowedGroupSids
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assigned = allowedGroups.Count > 0 && desiredGroups.Overlaps(allowedGroups);
        var directoryAllowsAccess = snapshot is { IsEnabled: true } && assigned;
        var locallySuspended = !user.IsActive && directoryAllowsAccess;
        var desiredActive = user.IsActive && directoryAllowsAccess;
        var desiredTombstone = snapshot is null;
        var desiredRole = snapshot is null
            ? user.Role
            : GlobalRoleResolver.Resolve(desiredGroups, state.Options.GlobalRoleMappings);
        var authorizationReduced = !desiredActive
            || desiredRole < user.Role
            || oldGroupKeys.Except(desiredGroups, StringComparer.OrdinalIgnoreCase).Any();
        var securityChanged = user.IsActive != desiredActive
            || user.IsTombstoned != desiredTombstone
            || user.Role != desiredRole
            || !oldGroupKeys.SetEquals(desiredGroups);

        user.IsActive = desiredActive;
        user.IsTombstoned = desiredTombstone;
        user.Role = desiredRole;
        user.LastDirectorySyncAt = state.AttemptTime;
        user.DirectorySyncStatus = snapshot is null
            ? "Missing"
            : !snapshot.IsEnabled
                ? "Disabled"
                : !assigned
                    ? "AccessRevoked"
                    : locallySuspended ? "LocallyDisabled" : "Current";
        user.KnownGroupSidsJson = JsonSerializer.Serialize(desiredGroups.OrderBy(group => group));
        identity.LastSeenAt = snapshot is null ? identity.LastSeenAt : state.AttemptTime;

        foreach (var membership in oldGroups)
        {
            if (!desiredGroups.Contains(membership.GroupKey))
                db.DirectoryMemberships.Remove(membership);
            else
                membership.LastSeenAt = state.AttemptTime;
        }
        foreach (var group in desiredGroups.Where(group => !oldGroupKeys.Contains(group)))
        {
            db.DirectoryMemberships.Add(new DirectoryMembership
            {
                UserId = user.Id,
                Authority = ExternalIdentity.ActiveDirectoryAuthority,
                GroupKey = group,
                LastSeenAt = state.AttemptTime,
            });
        }

        IReadOnlyList<Guid> executionIds = [];
        if (securityChanged)
        {
            UserSessionInvalidation.BumpSecurityStamp(user);
            var sessions = await db.AuthSessions
                .Where(session => session.UserId == user.Id && session.RevokedAt == null)
                .ToListAsync(ct);
            foreach (var session in sessions) session.RevokedAt = state.AttemptTime;
            if (authorizationReduced)
            {
                executionIds = await ExternalExecutionCancellation.CancelAsync(
                    db,
                    [user.Id],
                    state.AttemptTime,
                    "directory-offboarding",
                    "Execution cancelled because its effective directory principal lost authorization.",
                    ct);
            }
        }

        AuditLogEntry? audit = null;
        if (securityChanged)
        {
            audit = auditStager.Build(
                desiredActive
                    ? AuditActions.UserDirectorySynced
                    : AuditActions.UserDirectoryDeprovisioned,
                AuditActor.System,
                "User",
                user.Id,
                AuditDetails.Json(
                    ("status", user.DirectorySyncStatus),
                    ("authorizationChanged", true),
                    ("groupCount", desiredGroups.Count)));
            audit.Id = state.AuditId;
            db.AuditLog.Add(audit);
        }

        await db.SaveChangesAsync(ct);
        return new SyncAttemptResult(
            false, false, false, user.Id, securityChanged, executionIds, audit);
    }

    private static async Task<bool> VerifyAttemptSucceededAsync(
        NodePilotDbContext db,
        SyncAttemptState state,
        CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        if (await db.AuditLog.AsNoTracking().AnyAsync(entry => entry.Id == state.AuditId, ct))
            return true;
        var user = await db.ExternalIdentities.AsNoTracking()
            .Where(identity => identity.Id == state.IdentityId)
            .Select(identity => identity.User)
            .SingleOrDefaultAsync(ct);
        if (user is null || user.IsTombstoned) return true;
        if (state.Lookup is null || state.Lookup.Error is not null || state.RejectNotFound)
            return user.DirectorySyncStatus == "Failed";
        return user.LastDirectorySyncAt == state.AttemptTime;
    }

    private bool HasExpectedLease(long expectedLeaseEpoch) =>
        cluster.IsLeader && cluster.LeaseEpoch == expectedLeaseEpoch;

    private async Task<bool> AcquireDbLeaseFenceAsync(
        NodePilotDbContext db,
        long expectedLeaseEpoch,
        CancellationToken ct)
    {
        const string resource = "primary";
        var ownerNodeId = cluster.NodeId;
        var provider = db.Database.ProviderName;
        int affected;
        if (provider == "Microsoft.EntityFrameworkCore.SqlServer")
        {
            affected = await db.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE [ClusterLeaders] WITH (UPDLOCK, HOLDLOCK)
SET [LastRenewedAt] = [LastRenewedAt]
WHERE [Resource] = {resource}
  AND [OwnerNodeId] = {ownerNodeId}
  AND [LeaseEpoch] = {expectedLeaseEpoch}
  AND [ExpiresAt] > SYSUTCDATETIME()", ct);
        }
        else if (provider == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            affected = await db.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE ""ClusterLeaders""
SET ""LastRenewedAt"" = ""LastRenewedAt""
WHERE ""Resource"" = {resource}
  AND ""OwnerNodeId"" = {ownerNodeId}
  AND ""LeaseEpoch"" = {expectedLeaseEpoch}
  AND ""ExpiresAt"" > CURRENT_TIMESTAMP", ct);
        }
        else
        {
            var now = DateTime.UtcNow;
            affected = await db.ClusterLeaders
                .Where(lease => lease.Resource == resource
                                && lease.OwnerNodeId == ownerNodeId
                                && lease.LeaseEpoch == expectedLeaseEpoch
                                && lease.ExpiresAt > now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(lease => lease.LastRenewedAt, lease => lease.LastRenewedAt), ct);
        }
        return affected == 1;
    }

    private async Task SignalEngineOwnedExecutionsAsync(
        IServiceProvider services,
        IReadOnlyCollection<Guid> executionIds)
    {
        var engine = services.GetService<IWorkflowEngine>();
        if (engine is null || executionIds.Count == 0) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await ExternalExecutionCancellation.SignalAfterCommitAsync(
                engine, executionIds, "directory-offboarding", timeout.Token, logger);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            logger.LogWarning(
                "Timed out signalling committed directory-sync offboarding after 5 seconds");
        }
    }

    private sealed record SyncAttemptState(
        Guid IdentityId,
        LdapDirectoryLookupResult? Lookup,
        bool RejectNotFound,
        LdapOptions Options,
        long ExpectedLeaseEpoch,
        DateTime AttemptTime,
        Guid AuditId);

    private sealed record SyncAttemptResult(
        bool LeaseLost,
        bool Skipped,
        bool FailedLookup,
        Guid UserId,
        bool SecurityChanged,
        IReadOnlyList<Guid> ExecutionIds,
        AuditLogEntry? Audit)
    {
        public static readonly SyncAttemptResult LeaseWasLost =
            new(true, false, false, Guid.Empty, false, [], null);
        public static readonly SyncAttemptResult Skip =
            new(false, true, false, Guid.Empty, false, [], null);
    }
}
