using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NodePilot.Api.Security;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Api.Controllers;

public sealed record ResolveAdIdentityConflictRequest(
    string CanonicalSid,
    string LegacyLdapObjectGuid,
    Guid WinnerUserId);

public sealed record ResolveUpgradeIdentityConflictRequest(
    AuthProvider Provider,
    string ConflictExternalId,
    Guid WinnerUserId,
    IReadOnlyCollection<Guid> LoserUserIds);

public sealed record UpgradeIdentityConflictCandidate(
    Guid Id,
    string Username,
    UserRole Role,
    bool IsActive,
    bool IsTombstoned);

public sealed record UpgradeIdentityConflict(
    AuthProvider Provider,
    string ConflictExternalId,
    IReadOnlyList<UpgradeIdentityConflictCandidate> Candidates);

/// <summary>
/// Explicit, auditable resolution for pre-upgrade LDAP-objectGUID and Windows-SID rows
/// that represent the same AD person. History is never merged: the selected winner keeps
/// its history and receives the canonical SID identity; the loser is retained as a
/// tombstone with its existing historical foreign keys.
/// </summary>
[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/admin/external-identities")]
public sealed class ExternalIdentityResolutionController(
    NodePilotDbContext db,
    IAuditStager auditStager,
    IMemoryCache cache,
    IWorkflowEngine? workflowEngine = null,
    ILogger<ExternalIdentityResolutionController>? logger = null) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var identities = await db.ExternalIdentities.AsNoTracking()
            .OrderBy(identity => identity.Authority)
            .ThenBy(identity => identity.Subject)
            .Select(identity => new
            {
                identity.Id,
                identity.Authority,
                identity.Subject,
                identity.UserId,
                identity.User.Username,
                identity.User.Provider,
                identity.User.IsActive,
                identity.User.IsTombstoned,
            })
            .ToListAsync(ct);
        return Ok(identities);
    }

    /// <summary>
    /// Discovers exact duplicate LDAP objectGUID and Windows SID compatibility keys that
    /// the identity-model migration deliberately skipped. This reads the compatibility
    /// columns directly and therefore also finds conflicts with no ExternalIdentity row.
    /// </summary>
    [HttpGet("upgrade-conflicts")]
    public async Task<ActionResult<IReadOnlyList<UpgradeIdentityConflict>>> ListUpgradeConflicts(
        CancellationToken ct)
    {
        var compatibilityRows = await db.Users.AsNoTracking()
            .Where(user => (user.Provider == AuthProvider.Ldap
                            || user.Provider == AuthProvider.Windows)
                           && user.ExternalId != null)
            .Select(user => new
            {
                user.Id,
                user.Username,
                user.Provider,
                user.ExternalId,
                user.Role,
                user.IsActive,
                user.IsTombstoned,
            })
            .ToListAsync(ct);

        // Group in memory so SQL Server's database-default case-insensitive collation
        // cannot collapse distinct compatibility keys into a false conflict.
        var conflicts = compatibilityRows
            .GroupBy(row => (row.Provider, ExternalId: row.ExternalId!),
                EqualityComparer<(AuthProvider Provider, string ExternalId)>.Default)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key.Provider)
            .ThenBy(group => group.Key.ExternalId, StringComparer.Ordinal)
            .Select(group => new UpgradeIdentityConflict(
                group.Key.Provider,
                group.Key.ExternalId,
                group.OrderBy(row => row.Username, StringComparer.Ordinal)
                    .ThenBy(row => row.Id)
                    .Select(row => new UpgradeIdentityConflictCandidate(
                        row.Id,
                        row.Username,
                        row.Role,
                        row.IsActive,
                        row.IsTombstoned))
                    .ToList()))
            .ToList();

        return Ok(conflicts);
    }

    [HttpPost("resolve-ad-conflict")]
    public async Task<IActionResult> ResolveAdConflict(
        ResolveAdIdentityConflictRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CanonicalSid)
            || string.IsNullOrWhiteSpace(request.LegacyLdapObjectGuid)
            || request.CanonicalSid.Length > 256
            || request.LegacyLdapObjectGuid.Length > 256)
        {
            return BadRequest(new
            {
                message = "canonicalSid and legacyLdapObjectGuid are required and limited to 256 characters",
            });
        }

        await using var localGate = await AdminAccountMutationGate.EnterLocalAsync(ct);
        var strategy = db.Database.CreateExecutionStrategy();
        Guid? committedWinner = null;
        Guid? committedLoser = null;
        AuditLogEntry? committedAudit = null;
        IReadOnlyList<Guid> committedExecutionIds = [];
        var result = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            committedExecutionIds = [];
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, ct);
            await AdminAccountMutationGate.AcquireTransactionLockAsync(db, ct);
            var canonical = await db.ExternalIdentities.Include(identity => identity.User)
                .SingleOrDefaultAsync(identity =>
                    identity.Authority == ExternalIdentity.ActiveDirectoryAuthority
                    && identity.Subject == request.CanonicalSid, ct);
            var legacy = await db.ExternalIdentities.Include(identity => identity.User)
                .SingleOrDefaultAsync(identity =>
                    identity.Authority == ExternalIdentity.LegacyLdapAuthority
                    && identity.Subject == request.LegacyLdapObjectGuid, ct);
            if (canonical is null || legacy is null)
                return (IActionResult)NotFound(new { message = "canonical and legacy identities must both exist" });
            if (canonical.UserId == legacy.UserId)
                return Conflict(new { message = "identities already resolve to the same user" });
            if (request.WinnerUserId != canonical.UserId && request.WinnerUserId != legacy.UserId)
                return BadRequest(new { message = "winnerUserId must own one of the conflicting identities" });

            if (!IsExpectedExternalUser(canonical.User)
                || !IsExpectedExternalUser(legacy.User)
                || legacy.User.Provider != AuthProvider.Ldap
                || !string.Equals(canonical.User.ExternalId, request.CanonicalSid, StringComparison.Ordinal)
                || !string.Equals(legacy.User.ExternalId, request.LegacyLdapObjectGuid, StringComparison.Ordinal))
            {
                return (IActionResult)Conflict(new
                {
                    message = "identity owners must be exact LDAP/Windows compatibility candidates",
                });
            }

            var winner = request.WinnerUserId == canonical.UserId ? canonical.User : legacy.User;
            var loser = request.WinnerUserId == canonical.UserId ? legacy.User : canonical.User;
            if (winner.IsTombstoned)
                return Conflict(new { message = "the selected winner is tombstoned" });
            var conflictUserIds = new[] { winner.Id, loser.Id };
            var unrelatedIdentity = await db.ExternalIdentities.AnyAsync(identity =>
                conflictUserIds.Contains(identity.UserId)
                && identity.Id != canonical.Id
                && identity.Id != legacy.Id, ct);
            if (unrelatedIdentity)
                return Conflict(new { message = "a conflict candidate owns an unrelated external identity" });

            var invariantFailure = await ValidateRetirementInvariantsAsync([loser], ct);
            if (invariantFailure is not null)
                return Conflict(new { message = invariantFailure });

            canonical.UserId = winner.Id;
            db.ExternalIdentities.Remove(legacy);
            winner.ExternalId = request.CanonicalSid;
            winner.LastDirectorySyncAt = null;
            winner.DirectorySyncStatus = "IdentityResolvedReauthRequired";
            UserSessionInvalidation.BumpSecurityStamp(winner);
            loser.ExternalId = null;
            loser.KnownGroupSidsJson = null;
            loser.IsActive = false;
            loser.IsTombstoned = true;
            loser.LastDirectorySyncAt = null;
            loser.DirectorySyncStatus = "MergedIdentityTombstone";
            UserSessionInvalidation.BumpSecurityStamp(loser);

            db.DirectoryMemberships.RemoveRange(await db.DirectoryMemberships
                .Where(membership => membership.UserId == loser.Id).ToListAsync(ct));
            await RevokeSessionsAsync([winner.Id, loser.Id], ct);
            var executionIds = await CancelExecutionsAsync([winner.Id, loser.Id], ct);

            var actorId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsed)
                ? parsed
                : (Guid?)null;
            var audit = auditStager.Build(
                AuditActions.UserExternalIdentityResolved,
                new AuditActor(
                    actorId,
                    User.FindFirstValue(ClaimTypes.Name),
                    HttpContext.Connection.RemoteIpAddress?.ToString()),
                "User",
                winner.Id,
                AuditDetails.Json(
                    ("winnerUserId", winner.Id),
                    ("retiredUserId", loser.Id),
                    ("canonicalSid", request.CanonicalSid),
                    ("legacyLdapObjectGuid", request.LegacyLdapObjectGuid),
                    ("historyMerged", false)));
            db.AuditLog.Add(audit);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            committedWinner = winner.Id;
            committedLoser = loser.Id;
            committedAudit = audit;
            committedExecutionIds = executionIds;
            return NoContent();
        });

        if (committedWinner is { } winnerId && committedLoser is { } loserId)
        {
            UserSessionInvalidation.InvalidateUserStateCache(cache, winnerId);
            UserSessionInvalidation.InvalidateUserStateCache(cache, loserId);
        }
        if (committedAudit is not null)
            ForwardAudit(committedAudit);
        await SignalCommittedExecutionCancellationsAsync(committedExecutionIds);
        return result;
    }

    /// <summary>
    /// Resolves duplicate compatibility keys skipped by the identity-model migration. LDAP
    /// objectGUID duplicates receive one legacy identity; Windows SID duplicates receive one
    /// canonical AD identity. Candidate rows must be named explicitly and match the exact
    /// provider/key pair, so this endpoint cannot become a general-purpose account merge.
    /// </summary>
    [HttpPost("resolve-upgrade-conflict")]
    public async Task<IActionResult> ResolveUpgradeConflict(
        ResolveUpgradeIdentityConflictRequest request,
        CancellationToken ct)
    {
        if (request.Provider is not (AuthProvider.Ldap or AuthProvider.Windows))
            return BadRequest(new { message = "provider must be Ldap or Windows" });
        if (string.IsNullOrWhiteSpace(request.ConflictExternalId)
            || request.ConflictExternalId.Length > 256)
        {
            return BadRequest(new
            {
                message = "conflictExternalId is required and limited to 256 characters",
            });
        }
        if (request.LoserUserIds is null
            || request.LoserUserIds.Count == 0
            || request.LoserUserIds.Count > 100
            || request.LoserUserIds.Contains(request.WinnerUserId)
            || request.LoserUserIds.Distinct().Count() != request.LoserUserIds.Count)
        {
            return BadRequest(new
            {
                message = "loserUserIds must contain between 1 and 100 distinct users and exclude the winner",
            });
        }

        await using var localGate = await AdminAccountMutationGate.EnterLocalAsync(ct);
        var strategy = db.Database.CreateExecutionStrategy();
        var committedUserIds = new List<Guid>();
        AuditLogEntry? committedAudit = null;
        IReadOnlyList<Guid> committedExecutionIds = [];
        var result = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            committedUserIds.Clear();
            committedExecutionIds = [];
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, ct);
            await AdminAccountMutationGate.AcquireTransactionLockAsync(db, ct);

            // The SQL Server compatibility column may use a case-insensitive collation.
            // Filter in memory as well so only the exact legacy key can be retired.
            var databaseCandidates = await db.Users
                .Where(user => user.Provider == request.Provider
                               && user.ExternalId == request.ConflictExternalId)
                .ToListAsync(ct);
            var candidates = databaseCandidates
                .Where(user => string.Equals(
                    user.ExternalId, request.ConflictExternalId, StringComparison.Ordinal))
                .ToList();
            var requestedIds = request.LoserUserIds
                .Append(request.WinnerUserId)
                .ToHashSet();
            if (candidates.Count < 2 || !requestedIds.SetEquals(candidates.Select(user => user.Id)))
            {
                return (IActionResult)Conflict(new
                {
                    message = "winner and losers must name every exact provider/externalId conflict candidate",
                });
            }
            if (candidates.Any(user => !IsExpectedExternalUser(user)))
            {
                return Conflict(new
                {
                    message = "all conflict candidates must be LDAP/Windows users without local credentials",
                });
            }

            var winner = candidates.Single(user => user.Id == request.WinnerUserId);
            var losers = candidates.Where(user => user.Id != winner.Id).ToList();
            if (!winner.IsActive || winner.IsTombstoned)
                return Conflict(new { message = "the selected winner must be active and not tombstoned" });

            var candidateIds = candidates.Select(user => user.Id).ToList();
            var authority = request.Provider == AuthProvider.Ldap
                ? ExternalIdentity.LegacyLdapAuthority
                : ExternalIdentity.ActiveDirectoryAuthority;
            var linkedIdentities = await db.ExternalIdentities
                .Where(identity => candidateIds.Contains(identity.UserId))
                .ToListAsync(ct);
            if (linkedIdentities.Any(identity =>
                    identity.UserId != winner.Id
                    || !string.Equals(identity.Authority, authority, StringComparison.Ordinal)
                    || !string.Equals(identity.Subject, request.ConflictExternalId, StringComparison.Ordinal)))
            {
                return Conflict(new
                {
                    message = "a conflict candidate already owns an identity outside this exact upgrade conflict",
                });
            }

            var existingIdentity = await db.ExternalIdentities.SingleOrDefaultAsync(identity =>
                identity.Authority == authority
                && identity.Subject == request.ConflictExternalId, ct);
            if (existingIdentity is not null && existingIdentity.UserId != winner.Id)
            {
                return Conflict(new
                {
                    message = "the target external identity is already assigned to another user",
                });
            }

            var invariantFailure = await ValidateRetirementInvariantsAsync(losers, ct);
            if (invariantFailure is not null)
                return Conflict(new { message = invariantFailure });

            if (existingIdentity is null)
            {
                db.ExternalIdentities.Add(new ExternalIdentity
                {
                    Id = Guid.NewGuid(),
                    UserId = winner.Id,
                    Authority = authority,
                    Subject = request.ConflictExternalId,
                    CreatedAt = DateTime.UtcNow,
                    LastSeenAt = DateTime.UtcNow,
                });
            }

            winner.LastDirectorySyncAt = null;
            winner.DirectorySyncStatus = "IdentityResolvedReauthRequired";
            UserSessionInvalidation.BumpSecurityStamp(winner);
            foreach (var loser in losers)
            {
                loser.ExternalId = null;
                loser.KnownGroupSidsJson = null;
                loser.IsActive = false;
                loser.IsTombstoned = true;
                loser.LastDirectorySyncAt = null;
                loser.DirectorySyncStatus = "MergedIdentityTombstone";
                UserSessionInvalidation.BumpSecurityStamp(loser);
            }

            var loserIds = losers.Select(user => user.Id).ToList();
            db.DirectoryMemberships.RemoveRange(await db.DirectoryMemberships
                .Where(membership => loserIds.Contains(membership.UserId))
                .ToListAsync(ct));
            await RevokeSessionsAsync(candidateIds, ct);
            var executionIds = await CancelExecutionsAsync(candidateIds, ct);

            var actorId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsed)
                ? parsed
                : (Guid?)null;
            var audit = auditStager.Build(
                AuditActions.UserExternalIdentityResolved,
                new AuditActor(
                    actorId,
                    User.FindFirstValue(ClaimTypes.Name),
                    HttpContext.Connection.RemoteIpAddress?.ToString()),
                "User",
                winner.Id,
                AuditDetails.Json(
                    ("winnerUserId", winner.Id),
                    ("retiredUserIds", loserIds),
                    ("provider", request.Provider.ToString()),
                    ("conflictExternalId", request.ConflictExternalId),
                    ("identityAuthority", authority),
                    ("historyMerged", false)));
            db.AuditLog.Add(audit);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            committedUserIds.AddRange(candidateIds);
            committedAudit = audit;
            committedExecutionIds = executionIds;
            return NoContent();
        });

        foreach (var userId in committedUserIds)
            UserSessionInvalidation.InvalidateUserStateCache(cache, userId);
        if (committedAudit is not null)
            ForwardAudit(committedAudit);
        await SignalCommittedExecutionCancellationsAsync(committedExecutionIds);
        return result;
    }

    private void ForwardAudit(AuditLogEntry entry)
    {
        if (logger is null) return;
        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["event.action"] = entry.Action,
            ["event.category"] = "iam",
            ["event.kind"] = "event",
            ["event.outcome"] = "success",
            ["event.dataset"] = "nodepilot.audit",
            ["event.id"] = entry.Id.ToString(),
            ["user.id"] = entry.UserId?.ToString(),
            ["user.name"] = entry.Username,
            ["source.ip"] = entry.IpAddress,
            ["AuditResourceType"] = entry.ResourceType,
            ["AuditResourceId"] = entry.ResourceId?.ToString(),
            ["SupportLog"] = true,
        }))
        {
            logger.LogInformation("AUDIT {Action} resource={ResourceType}/{ResourceId}",
                entry.Action, entry.ResourceType, entry.ResourceId);
        }
    }

    private async Task RevokeSessionsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var sessions = await db.AuthSessions
            .Where(session => userIds.Contains(session.UserId) && session.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var session in sessions) session.RevokedAt = now;
    }

    private async Task<string?> ValidateRetirementInvariantsAsync(
        IReadOnlyCollection<User> retiredUsers,
        CancellationToken ct)
    {
        var retiredUserIds = retiredUsers.Select(user => user.Id).ToList();
        if (retiredUsers.Any(user =>
                user.Role == UserRole.Admin && user.IsActive && !user.IsTombstoned))
        {
            var activeAdminSurvives = await db.Users.AsNoTracking().AnyAsync(user =>
                !retiredUserIds.Contains(user.Id)
                && user.Role == UserRole.Admin
                && user.IsActive
                && !user.IsTombstoned, ct);
            if (!activeAdminSurvives)
                return "resolution would retire the last active Admin";
        }

        if (retiredUsers.Any(BreakGlassAccountPolicy.IsRecoveryCapable))
        {
            var recoveryAccountSurvives = await db.Users.AsNoTracking().AnyAsync(user =>
                !retiredUserIds.Contains(user.Id)
                && user.Provider == AuthProvider.Local
                && user.Role == UserRole.Admin
                && user.IsActive
                && !user.IsTombstoned
                && user.IsBreakGlass
                && user.PasswordHash != null
                && user.PasswordHash != string.Empty, ct);
            if (!recoveryAccountSurvives)
                return "resolution would retire the last local break-glass Admin";
        }

        return null;
    }

    private static bool IsExpectedExternalUser(User user) =>
        user.Provider is AuthProvider.Ldap or AuthProvider.Windows
        && string.IsNullOrEmpty(user.PasswordHash);

    private async Task<IReadOnlyList<Guid>> CancelExecutionsAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return await ExternalExecutionCancellation.CancelAsync(
            db,
            userIds,
            now,
            "identity-conflict-resolution",
            "Execution cancelled because its external identity conflict was resolved.",
            ct);
    }

    private async Task SignalCommittedExecutionCancellationsAsync(
        IReadOnlyCollection<Guid> executionIds)
    {
        if (workflowEngine is null || executionIds.Count == 0) return;
        using var signalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await ExternalExecutionCancellation.SignalAfterCommitAsync(
                workflowEngine,
                executionIds,
                "identity-conflict-resolution",
                signalTimeout.Token,
                logger);
        }
        catch (OperationCanceledException) when (signalTimeout.IsCancellationRequested)
        {
            logger?.LogError(
                "Post-commit identity conflict execution cancellation signal timed out for {Count} execution(s)",
                executionIds.Count);
        }
    }
}
