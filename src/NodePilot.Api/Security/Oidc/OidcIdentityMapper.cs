using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Api.Security.Oidc;

internal enum OidcMapFailure
{
    None,
    InvalidIdentity,
    AccessNotAssigned,
    BootstrapRequired,
    UsernameCollision,
    AccountDisabled,
    LastAdmin,
}

internal sealed record OidcMapResult(User? User, OidcMapFailure Failure, string AuditReason)
{
    public bool Succeeded => User is not null && Failure == OidcMapFailure.None;

    public static OidcMapResult Success(User user) => new(user, OidcMapFailure.None, "success");
    public static OidcMapResult Denied(OidcMapFailure failure, string reason) => new(null, failure, reason);
}

/// <summary>
/// Resolves an already validated OIDC principal by its immutable issuer + subject tuple.
/// Human-readable claims are deliberately never used to link an existing account.
/// </summary>
public sealed class OidcIdentityMapper(
    NodePilotDbContext db,
    IOptions<EnterpriseOidcOptions> options,
    ILogger<OidcIdentityMapper> logger,
    IOptions<AuthenticationPolicyOptions>? authenticationPolicy = null,
    IMemoryCache? userStateCache = null,
    IWorkflowEngine? workflowEngine = null,
    IAuditStager? auditStager = null)
{
    internal const int MaximumGroups = 1_000;
    // Keeps the composite unique index below SQL Server's 1700-byte key limit.
    private const int MaximumIdentityPartLength = 384;
    private const int MaximumGroupIdLength = 256;

    internal async Task<OidcMapResult> MapAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var current = options.Value;
        var issuer = principal.FindFirstValue("iss");
        var subject = principal.FindFirstValue("sub");
        if (!IsValidIssuer(issuer)
            || !string.Equals(issuer, current.Authority, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(subject)
            || !string.Equals(subject, subject.Trim(), StringComparison.Ordinal)
            || subject.Length > MaximumIdentityPartLength
            || subject.Any(char.IsControl))
        {
            return OidcMapResult.Denied(OidcMapFailure.InvalidIdentity, "oidc_invalid_issuer_or_subject");
        }

        var allowedGroups = current.AllowedGroupIds
            .Where(IsValidGroupId)
            .ToHashSet(StringComparer.Ordinal);
        if (allowedGroups.Count == 0)
            return OidcMapResult.Denied(OidcMapFailure.AccessNotAssigned, "oidc_allowed_groups_not_configured");

        var groupSnapshot = ReadGroupSnapshot(principal, current.GroupsClaimType);
        var hasOverageSignal = HasGroupOverageSignal(principal, current.GroupsClaimType);
        var groups = groupSnapshot.Groups;
        var groupsFromToken = groupSnapshot.IsComplete && !hasOverageSignal;
        var maxStaleness = Math.Clamp(
            authenticationPolicy?.Value.MaxAuthorizationStalenessMinutes ?? 15, 1, 15);
        var tokenObservedAt = groupsFromToken ? ReadTokenIssuedAt(principal) : null;
        if (groupsFromToken
            && (tokenObservedAt is null
                || tokenObservedAt > DateTime.UtcNow.AddMinutes(1)
                || DateTime.UtcNow - tokenObservedAt > TimeSpan.FromMinutes(maxStaleness)))
        {
            return OidcMapResult.Denied(
                OidcMapFailure.InvalidIdentity, "oidc_group_snapshot_timestamp_invalid_or_stale");
        }
        if (groupSnapshot.ClaimPresent && !groupSnapshot.IsComplete && !hasOverageSignal)
        {
            return OidcMapResult.Denied(
                OidcMapFailure.AccessNotAssigned, "oidc_group_snapshot_incomplete");
        }
        DateTime? membershipSnapshotObservedAt = null;
        var identityCandidates = await db.ExternalIdentities
            .Include(x => x.User)
            .Where(x => x.Authority == issuer && x.Subject == subject)
            .Take(3)
            .ToListAsync(ct);
        // SQL Server commonly runs under a case-insensitive collation. Never trust that
        // coarse database comparison for OIDC's opaque, case-sensitive identifiers.
        var identity = identityCandidates.SingleOrDefault(x =>
            string.Equals(x.Authority, issuer, StringComparison.Ordinal)
            && string.Equals(x.Subject, subject, StringComparison.Ordinal));
        if (identity is null && identityCandidates.Count > 0)
            return OidcMapResult.Denied(OidcMapFailure.InvalidIdentity, "oidc_identity_collation_conflict");
        // OIDC providers such as Entra omit the groups claim once a user crosses their
        // token-size threshold. For a pre-provisioned SCIM identity, use the normalized
        // server-side memberships instead of rejecting a legitimate enterprise user or
        // inflating the browser cookie.
        if (identity is not null && hasOverageSignal)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-maxStaleness);
            var freshMemberships = await db.DirectoryMemberships
                    .Where(membership => membership.UserId == identity.UserId
                                      && membership.Authority == issuer
                                      && membership.LastSeenAt >= cutoff)
                    .Take(MaximumGroups)
                    .ToListAsync(ct);
            var validMemberships = freshMemberships
                .Where(membership => string.Equals(
                                          membership.Authority, issuer, StringComparison.Ordinal)
                                      && IsValidGroupId(membership.GroupKey))
                .ToList();
            groups = validMemberships
                .Select(membership => membership.GroupKey)
                .ToHashSet(StringComparer.Ordinal);
            if (validMemberships.Count > 0)
                membershipSnapshotObservedAt = validMemberships.Min(membership => membership.LastSeenAt);
        }

        // A complete token snapshot is authoritative even when it proves that an existing
        // user has just lost the required access group. Reconcile and offboard first, then
        // deny the callback. Missing/malformed/overage claims never enter this destructive
        // path; overage uses the server-side SCIM snapshot instead.
        if (identity is not null && groupsFromToken)
        {
            return await ReconcileExistingAsync(
                issuer!, subject, groups, tokenObservedAt!.Value, groupsFromToken: true,
                allowedGroups, current.GlobalRoleMappings, maxStaleness, ct);
        }
        if (identity is null && !groupsFromToken)
        {
            return OidcMapResult.Denied(
                OidcMapFailure.AccessNotAssigned, "oidc_complete_group_snapshot_required_for_jit");
        }
        if (!groups.Overlaps(allowedGroups))
            return OidcMapResult.Denied(OidcMapFailure.AccessNotAssigned, "oidc_required_group_missing");

        var role = ResolveRole(groups, current.GlobalRoleMappings);

        if (identity is null)
        {
            // External authentication is never the installation bootstrap. An operator must
            // first create the explicit local break-glass administrator.
            if (!await BreakGlassAccountPolicy.ExistsAsync(db, ct))
                return OidcMapResult.Denied(OidcMapFailure.BootstrapRequired, "oidc_bootstrap_required");

            var username = SelectUsername(principal, current.NameClaimType, subject);
            if (await db.Users.AnyAsync(x => x.Username == username, ct))
                return OidcMapResult.Denied(OidcMapFailure.UsernameCollision, "oidc_username_collision");

            var now = DateTime.UtcNow;
            var initialAuthorizationObservedAt = tokenObservedAt!.Value;
            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = username,
                PasswordHash = null,
                Provider = AuthProvider.Oidc,
                ExternalId = subject,
                Role = role,
                IsActive = true,
                CreatedAt = now,
                PasswordChangedAt = now,
                LastDirectorySyncAt = initialAuthorizationObservedAt,
                DirectorySyncStatus = "OidcTokenCurrent",
            };
            user.ExternalIdentities.Add(new ExternalIdentity
            {
                Id = Guid.NewGuid(),
                Authority = issuer!,
                Subject = subject,
                CreatedAt = now,
                LastSeenAt = now,
            });
            db.Users.Add(user);
            await SyncMembershipsAsync(user.Id, issuer!, groups, initialAuthorizationObservedAt, ct);

            try
            {
                await db.SaveChangesAsync(ct);
                if (userStateCache is not null)
                    UserSessionInvalidation.InvalidateUserStateCache(userStateCache, user.Id);
                return OidcMapResult.Success(user);
            }
            catch (DbUpdateException ex)
            {
                logger.LogWarning(ex,
                    "Concurrent OIDC JIT provisioning was rejected for issuer {Issuer}", issuer);
                db.ChangeTracker.Clear();
                var concurrentCandidates = await db.ExternalIdentities
                    .Include(x => x.User)
                    .Where(x => x.Authority == issuer && x.Subject == subject)
                    .Take(3)
                    .ToListAsync(ct);
                var concurrentlyCreated = concurrentCandidates.SingleOrDefault(x =>
                    string.Equals(x.Authority, issuer, StringComparison.Ordinal)
                    && string.Equals(x.Subject, subject, StringComparison.Ordinal));
                if (concurrentlyCreated?.User is { IsActive: true, IsTombstoned: false })
                {
                    // Re-enter the normal existing-user path with this callback's claims.
                    // Returning the winner directly would mint a session with whichever
                    // role/groups the other node happened to persist (possibly Admin).
                    db.ChangeTracker.Clear();
                    return await MapAsync(principal, ct);
                }
                return OidcMapResult.Denied(
                    OidcMapFailure.UsernameCollision, "oidc_concurrent_provisioning_conflict");
            }
        }

        return await ReconcileExistingAsync(
            issuer!, subject, groups, membershipSnapshotObservedAt, groupsFromToken: false,
            allowedGroups, current.GlobalRoleMappings, maxStaleness, ct);
    }

    private async Task<OidcMapResult> ReconcileExistingAsync(
        string issuer,
        string subject,
        IReadOnlySet<string> presentedGroups,
        DateTime? presentedObservedAt,
        bool groupsFromToken,
        IReadOnlySet<string> allowedGroups,
        IEnumerable<OidcRoleMapping> roleMappings,
        int maxStalenessMinutes,
        CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        IReadOnlyList<Guid> executionIdsToSignal = [];
        Guid? committedUserId = null;
        AuditLogEntry? committedAudit = null;

        OidcMapResult result;
        await using (var adminMutation = await AdminAccountMutationGate.EnterLocalAsync(ct))
        {
            result = await strategy.ExecuteAsync(async () =>
        {
            // Every retry starts from database state, never from entities tracked by the
            // failed attempt. Role, group, session and execution changes then commit as one
            // serializable authorization decision.
            db.ChangeTracker.Clear();
            executionIdsToSignal = [];
            committedUserId = null;
            committedAudit = null;

            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, ct);
            await AdminAccountMutationGate.AcquireTransactionLockAsync(db, ct);
            try
            {
                var identityCandidates = await db.ExternalIdentities
                    .Include(candidate => candidate.User)
                    .Where(candidate => candidate.Authority == issuer
                                     && candidate.Subject == subject)
                    .Take(3)
                    .ToListAsync(ct);
                var identity = identityCandidates.SingleOrDefault(candidate =>
                    string.Equals(candidate.Authority, issuer, StringComparison.Ordinal)
                    && string.Equals(candidate.Subject, subject, StringComparison.Ordinal));
                if (identity is null)
                {
                    await transaction.RollbackAsync(ct);
                    db.ChangeTracker.Clear();
                    return OidcMapResult.Denied(
                        OidcMapFailure.InvalidIdentity, "oidc_identity_changed_during_reconciliation");
                }

                var existing = identity.User;
                if (!existing.IsActive || existing.IsTombstoned)
                {
                    await transaction.RollbackAsync(ct);
                    db.ChangeTracker.Clear();
                    return OidcMapResult.Denied(
                        OidcMapFailure.AccountDisabled, "oidc_account_disabled");
                }

                HashSet<string> groups;
                DateTime? authorizationObservedAt;
                if (groupsFromToken)
                {
                    groups = presentedGroups.ToHashSet(StringComparer.Ordinal);
                    authorizationObservedAt = presentedObservedAt;
                }
                else
                {
                    var cutoff = DateTime.UtcNow.AddMinutes(-maxStalenessMinutes);
                    var memberships = await db.DirectoryMemberships
                        .Where(membership => membership.UserId == existing.Id
                                          && membership.Authority == issuer
                                          && membership.LastSeenAt >= cutoff)
                        .Take(MaximumGroups)
                        .ToListAsync(ct);
                    var exactMemberships = memberships
                        .Where(membership => string.Equals(
                                                  membership.Authority, issuer,
                                                  StringComparison.Ordinal)
                                              && IsValidGroupId(membership.GroupKey))
                        .ToList();
                    groups = exactMemberships
                        .Select(membership => membership.GroupKey)
                        .ToHashSet(StringComparer.Ordinal);
                    authorizationObservedAt = exactMemberships.Count == 0
                        ? null
                        : exactMemberships.Min(membership => membership.LastSeenAt);
                }

                var oldMemberships = await db.DirectoryMemberships
                    .Where(membership => membership.UserId == existing.Id
                                      && membership.Authority == issuer)
                    .ToListAsync(ct);
                var oldGroups = oldMemberships
                    .Where(membership => string.Equals(
                                              membership.Authority, issuer,
                                              StringComparison.Ordinal))
                    .Select(membership => membership.GroupKey)
                    .ToHashSet(StringComparer.Ordinal);
                var accessAssigned = groups.Overlaps(allowedGroups);
                var desiredRole = ResolveRole(groups, roleMappings);
                var oldRole = existing.Role;
                var lastAdminDemotion = oldRole == UserRole.Admin
                                        && desiredRole != UserRole.Admin
                                        && await db.Users.CountAsync(
                                            user => user.IsActive && user.Role == UserRole.Admin, ct) <= 1;
                var persistedRole = lastAdminDemotion ? oldRole : desiredRole;
                var groupsChanged = groupsFromToken && !groups.SetEquals(oldGroups);
                var targetAuthorizationStatus = !accessAssigned
                    ? (groupsFromToken ? "OidcAccessDenied" : "ScimAccessDenied")
                    : lastAdminDemotion
                        ? (groupsFromToken ? "OidcLastAdminRefused" : "ScimLastAdminRefused")
                        : groupsFromToken ? "OidcTokenCurrent" : "ScimCurrent";
                var authorizationChanged = persistedRole != oldRole
                                           || groupsChanged
                                           || !string.Equals(
                                               existing.DirectorySyncStatus,
                                               targetAuthorizationStatus,
                                               StringComparison.Ordinal);
                var authorizationReduced = !accessAssigned
                                           || desiredRole < oldRole
                                           || (groupsFromToken
                                               && oldGroups.Except(groups, StringComparer.Ordinal).Any());
                var now = DateTime.UtcNow;

                existing.Role = persistedRole;
                existing.Provider = AuthProvider.Oidc;
                existing.ExternalId = subject;
                if (groupsFromToken)
                {
                    existing.LastDirectorySyncAt = authorizationObservedAt;
                    existing.DirectorySyncStatus = targetAuthorizationStatus;
                    ApplyMembershipSnapshot(
                        existing.Id, issuer, groups, authorizationObservedAt!.Value,
                        oldMemberships);
                }
                else
                {
                    if (authorizationObservedAt is { } observedAt
                        && (existing.LastDirectorySyncAt is null
                            || existing.LastDirectorySyncAt < observedAt))
                    {
                        // Overage logins consume the SCIM observation time without extending it.
                        existing.LastDirectorySyncAt = observedAt;
                    }
                    existing.DirectorySyncStatus = targetAuthorizationStatus;
                }
                identity.LastSeenAt = now;
                if (authorizationChanged) existing.SecurityStamp++;

                var revokedSessionCount = 0;
                if (authorizationReduced)
                {
                    var sessions = await db.AuthSessions
                        .Where(session => session.UserId == existing.Id
                                          && session.RevokedAt == null)
                        .ToListAsync(ct);
                    foreach (var session in sessions) session.RevokedAt = now;
                    revokedSessionCount = sessions.Count;

                    // Only the durable terminal execution state belongs in this transaction.
                    // In-memory engine tokens are signalled after commit so a rollback never
                    // kills work whose authorization state remained valid.
                    executionIdsToSignal = await ExternalExecutionCancellation.CancelAsync(
                        db,
                        [existing.Id],
                        now,
                        "oidc-authorization-change",
                        "Execution cancelled because its OIDC principal authorization changed.",
                        ct);
                }

                if (authorizationChanged
                    || revokedSessionCount > 0
                    || executionIdsToSignal.Count > 0)
                {
                    var stager = auditStager ?? new AuditStager();
                    committedAudit = stager.Build(
                        AuditActions.UserDirectorySynced,
                        new AuditActor(existing.Id, existing.Username, null),
                        "User",
                        existing.Id,
                        AuditDetails.Json(
                            ("source", "Oidc"),
                            ("accessAssigned", accessAssigned),
                            ("oldRole", oldRole.ToString()),
                            ("desiredRole", desiredRole.ToString()),
                            ("persistedRole", persistedRole.ToString()),
                            ("lastAdminInvariant", lastAdminDemotion),
                            ("groupsChanged", groupsChanged)));
                    db.AuditLog.Add(committedAudit);
                }

                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                committedUserId = existing.Id;

                if (!accessAssigned)
                {
                    return OidcMapResult.Denied(
                        OidcMapFailure.AccessNotAssigned, "oidc_required_group_missing");
                }
                if (lastAdminDemotion)
                {
                    return OidcMapResult.Denied(
                        OidcMapFailure.LastAdmin, "oidc_last_admin_demotion_refused");
                }
                return OidcMapResult.Success(existing);
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                throw;
            }
        });
        }

        if (committedUserId is { } userId && userStateCache is not null)
            UserSessionInvalidation.InvalidateUserStateCache(userStateCache, userId);
        if (executionIdsToSignal.Count > 0)
        {
            // RequestAborted may fire immediately after the durable commit (browser close,
            // proxy timeout). The in-memory engine signal is security-critical and must not
            // inherit that HTTP token; bound it with its own short operational timeout.
            using var signalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await ExternalExecutionCancellation.SignalAfterCommitAsync(
                    workflowEngine,
                    executionIdsToSignal,
                    "oidc-authorization-change",
                    signalTimeout.Token,
                    logger);
            }
            catch (OperationCanceledException) when (signalTimeout.IsCancellationRequested)
            {
                logger.LogError(
                    "Post-commit OIDC execution cancellation signal timed out for user {UserId}",
                    committedUserId);
            }
        }
        if (committedAudit is not null)
        {
            logger.LogInformation(
                "OIDC authorization reconciliation committed for user {UserId} (audit {AuditId})",
                committedUserId, committedAudit.Id);
        }
        return result;
    }

    private void ApplyMembershipSnapshot(
        Guid userId,
        string authority,
        IReadOnlySet<string> groups,
        DateTime timestamp,
        IReadOnlyCollection<DirectoryMembership> existing)
    {
        db.DirectoryMemberships.RemoveRange(existing.Where(membership =>
            string.Equals(membership.Authority, authority, StringComparison.Ordinal)
            && !groups.Contains(membership.GroupKey)));
        foreach (var retained in existing.Where(membership =>
                     string.Equals(membership.Authority, authority, StringComparison.Ordinal)
                     && groups.Contains(membership.GroupKey)))
        {
            retained.LastSeenAt = timestamp;
        }

        var existingKeys = existing
            .Where(membership => string.Equals(
                membership.Authority, authority, StringComparison.Ordinal))
            .Select(membership => membership.GroupKey)
            .ToHashSet(StringComparer.Ordinal);
        db.DirectoryMemberships.AddRange(groups
            .Where(group => !existingKeys.Contains(group))
            .Select(group => new DirectoryMembership
            {
                UserId = userId,
                Authority = authority,
                GroupKey = group,
                LastSeenAt = timestamp,
            }));
    }

    private async Task SyncMembershipsAsync(
        Guid userId,
        string authority,
        IReadOnlySet<string> groups,
        DateTime timestamp,
        CancellationToken ct)
    {
        var existing = await db.DirectoryMemberships
            .Where(x => x.UserId == userId && x.Authority == authority)
            .ToListAsync(ct);
        db.DirectoryMemberships.RemoveRange(existing.Where(x => !groups.Contains(x.GroupKey)));
        foreach (var retained in existing.Where(x => groups.Contains(x.GroupKey)))
            retained.LastSeenAt = timestamp;
        var existingKeys = existing.Select(x => x.GroupKey).ToHashSet(StringComparer.Ordinal);
        db.DirectoryMemberships.AddRange(groups.Where(x => !existingKeys.Contains(x)).Select(group => new DirectoryMembership
        {
            UserId = userId,
            Authority = authority,
            GroupKey = group,
            LastSeenAt = timestamp,
        }));
    }

    internal static HashSet<string> ReadGroups(ClaimsPrincipal principal, string? claimType)
        => ReadGroupSnapshot(principal, claimType).Groups;

    private static GroupSnapshot ReadGroupSnapshot(
        ClaimsPrincipal principal,
        string? claimType)
    {
        var type = string.IsNullOrWhiteSpace(claimType) ? "groups" : claimType;
        var claims = principal.FindAll(type).ToList();
        var groups = new HashSet<string>(StringComparer.Ordinal);
        if (claims.Count == 0) return new GroupSnapshot(groups, false, false);

        var complete = true;
        foreach (var claim in claims)
        {
            var value = claim.Value;
            if (value.StartsWith("[", StringComparison.Ordinal))
            {
                try
                {
                    foreach (var item in JsonSerializer.Deserialize<string[]>(value) ?? [])
                    {
                        if (!IsValidGroupId(item))
                        {
                            complete = false;
                            continue;
                        }
                        if (groups.Count >= MaximumGroups && !groups.Contains(item))
                        {
                            complete = false;
                            continue;
                        }
                        groups.Add(item);
                    }
                }
                catch (JsonException)
                {
                    // Malformed JSON is neither interpreted as a group identifier nor
                    // accepted as an authoritative empty membership snapshot.
                    complete = false;
                }
            }
            else if (IsValidGroupId(value))
            {
                if (groups.Count >= MaximumGroups && !groups.Contains(value))
                    complete = false;
                else
                    groups.Add(value);
            }
            else
            {
                complete = false;
            }
        }
        return new GroupSnapshot(groups, true, complete);
    }

    private sealed record GroupSnapshot(
        HashSet<string> Groups,
        bool ClaimPresent,
        bool IsComplete);

    internal static bool HasGroupOverageSignal(ClaimsPrincipal principal, string? claimType)
    {
        if (principal.FindAll("hasgroups").Any(claim =>
                string.Equals(claim.Value, "true", StringComparison.OrdinalIgnoreCase)
                || claim.Value == "1"))
            return true;

        var configuredType = string.IsNullOrWhiteSpace(claimType) ? "groups" : claimType;
        foreach (var claim in principal.FindAll("_claim_names"))
        {
            try
            {
                using var document = JsonDocument.Parse(claim.Value);
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                if (document.RootElement.TryGetProperty(configuredType, out _)
                    || (configuredType != "groups"
                        && document.RootElement.TryGetProperty("groups", out _)))
                    return true;
            }
            catch (JsonException)
            {
                // Malformed overage metadata is not trusted.
            }
        }
        return false;
    }

    private static DateTime? ReadTokenIssuedAt(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(JwtRegisteredClaimNames.Iat)
                  ?? principal.FindFirstValue("iat");
        return long.TryParse(raw, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : null;
    }

    internal static UserRole ResolveRole(
        IReadOnlySet<string> groups,
        IEnumerable<OidcRoleMapping> mappings)
    {
        var role = UserRole.Viewer;
        foreach (var mapping in mappings)
        {
            if (IsValidGroupId(mapping.GroupId)
                && groups.Contains(mapping.GroupId)
                && mapping.Role > role)
            {
                role = mapping.Role;
            }
        }
        return role;
    }

    private static string SelectUsername(ClaimsPrincipal principal, string? configuredType, string subject)
    {
        var candidates = new[]
        {
            configuredType,
            "preferred_username",
            ClaimTypes.Email,
            "email",
            ClaimTypes.Name,
            "name",
        };
        foreach (var type in candidates.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct())
        {
            var value = principal.FindFirstValue(type!)?.Trim();
            if (!string.IsNullOrWhiteSpace(value)) return Truncate(value, 100);
        }
        return Truncate(subject, 100);
    }

    internal static bool IsValidIssuer(string? value)
        => value is { Length: > 0 and <= MaximumIdentityPartLength }
           && string.Equals(value, value.Trim(), StringComparison.Ordinal)
           && Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && string.IsNullOrEmpty(uri.Fragment)
           && string.IsNullOrEmpty(uri.Query)
           && string.IsNullOrEmpty(uri.UserInfo);

    internal static bool IsValidGroupId(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length <= MaximumGroupIdLength
           && string.Equals(value, value.Trim(), StringComparison.Ordinal)
           && !value.Any(char.IsControl);

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}
