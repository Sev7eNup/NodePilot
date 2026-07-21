using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NodePilot.Api.Audit;
using NodePilot.Api.Security;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Api.Security.Ldap;

/// <summary>
/// Outcome of <c>ExternalUserMapper.MapAsync</c>. Either a User row to mint a
/// session for, or a refusal that the calling login endpoint translates to a 401 +
/// audit entry.
/// </summary>
public enum ExternalUserMapResult
{
    /// <summary>JIT-provisioned (or already-mapped) user is ready to receive a session.</summary>
    Mapped,
    /// <summary>A user with the same Username already exists. Refuse rather than silently
    /// merging identities — the operator must resolve the collision explicitly.</summary>
    RefusedUsernameCollision,
    /// <summary>
    /// No active local break-glass administrator exists yet. External provisioning is
    /// blocked so it cannot close the <c>X-Setup-Token</c> bootstrap path.
    /// </summary>
    RefusedBootstrapNotAdmin,
    /// <summary>
    /// The directory mapping would demote the last active Admin. The login is refused,
    /// the stored Admin role is preserved, and existing sessions are invalidated.
    /// </summary>
    RefusedLastActiveAdmin,
    /// <summary>
    /// Canonical and legacy keys resolve to different users, or a legacy key is duplicated.
    /// Refuse instead of silently merging audit trails and privileges.
    /// </summary>
    RefusedIdentityConflict,
    /// <summary>A retained tombstone matched the external identity.</summary>
    RefusedTombstoned,
    /// <summary>The authenticated identity is not assigned to an allowed directory group.</summary>
    RefusedDirectoryAccess,
}

/// <summary>Envelope for a mapping outcome.</summary>
public sealed record ExternalUserMapping(ExternalUserMapResult Result, User? User, string? RefusalReason);

/// <summary>
/// Find-or-create the local <see cref="User"/> row that corresponds to a successful
/// directory authentication. Looks up by canonical <c>(Authority, Subject)</c> so a
/// renamed AD user keeps the same NodePilot row + audit trail.
/// <para>
/// Order of operations:
/// <list type="number">
/// <item>Match an existing JIT-provisioned row by canonical external identity. Update
/// <c>Role</c> and the authority-scoped membership snapshot.</item>
/// <item>If no identity exists and any user has the same Username, refuse and audit the
/// collision. Automatic identity merges are deliberately unsupported.</item>
/// <item>Otherwise create a new row. Username is the UPN so a local <c>alice</c> and an
/// LDAP <c>alice@firma.de</c> can coexist on the same instance. Optional Default-Folder
/// grant on Root if <see cref="LdapOptions.JitUserDefaultRootRole"/> is set.</item>
/// </list>
/// </para>
/// All audit entries (<c>USER_LDAP_JIT_CREATED</c>, <c>USER_LDAP_JIT_UPDATED</c>,
/// <c>USER_LDAP_REFUSED_COLLISION</c>) flow through
/// <see cref="IAuditWriter"/> on the same DbContext as the user mutation, so the audit
/// row commits atomically with the user row.
/// </summary>
public sealed class ExternalUserMapper
{
    // Keep the full detail JSON comfortably below AuditStager's 4 KiB cap even when
    // Windows SIDs use their documented maximum length. Counts + fingerprints still
    // preserve evidence for larger deltas.
    private const int MaxAuditedGroupSidDeltaEntries = 6;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> IdentityMutationGates = new(StringComparer.Ordinal);

    private readonly NodePilotDbContext _db;
    private readonly IOptionsMonitor<LdapOptions> _options;
    private readonly IAuditWriter _audit;
    private readonly IMemoryCache _userStateCache;
    private readonly ILogger<ExternalUserMapper> _logger;
    private readonly ActiveDirectoryAuthenticationConfiguration? _activeConfiguration;
    private readonly IWorkflowEngine? _workflowEngine;
    private readonly IAuditStager _auditStager;
    private readonly IHttpContextAccessor? _httpContextAccessor;

    public ExternalUserMapper(
        NodePilotDbContext db,
        IOptionsMonitor<LdapOptions> options,
        IAuditWriter audit,
        IMemoryCache userStateCache,
        ILogger<ExternalUserMapper> logger,
        ActiveDirectoryAuthenticationConfiguration? activeConfiguration = null,
        IWorkflowEngine? workflowEngine = null,
        IAuditStager? auditStager = null,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _db = db;
        _options = options;
        _audit = audit;
        _userStateCache = userStateCache;
        _logger = logger;
        _activeConfiguration = activeConfiguration;
        _workflowEngine = workflowEngine;
        _auditStager = auditStager ?? new AuditStager();
        _httpContextAccessor = httpContextAccessor;
    }

    public Task<ExternalUserMapping> MapAsync(LdapAuthResult ldapResult, CancellationToken ct) =>
        MapAsync(ldapResult, AuthProvider.Ldap, ct);

    /// <summary>
    /// Provider-parametric variant, added so the Windows-Negotiate path can reuse this
    /// mapper too: it constructs an <see cref="LdapAuthResult"/> from the OS token's SID +
    /// group SIDs and calls this with <see cref="AuthProvider.Windows"/>; the LDAP path
    /// keeps its convenience overload above.
    /// </summary>
    public async Task<ExternalUserMapping> MapAsync(LdapAuthResult ldapResult, AuthProvider provider, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ldapResult);
        if (provider is not (AuthProvider.Ldap or AuthProvider.Windows))
            throw new ArgumentException("ExternalUserMapper handles LDAP and Windows providers only.", nameof(provider));
        if (string.IsNullOrWhiteSpace(ldapResult.Subject))
            throw new ArgumentException("External identity subject must not be empty.", nameof(ldapResult));

        // Serialize only mutations for the same canonical identity. This closes the local
        // concurrent-JIT race without throttling unrelated users; the DB unique index is the
        // final guard across processes/nodes.
        var gateKey = ExternalIdentity.ActiveDirectoryAuthority + "\0" + ldapResult.Subject;
        var gate = IdentityMutationGates.GetOrAdd(gateKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await MapCoreAsync(ldapResult, provider, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ExternalUserMapping> MapCoreAsync(
        LdapAuthResult ldapResult,
        AuthProvider provider,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ldapResult);
        if (provider is not (AuthProvider.Ldap or AuthProvider.Windows))
            throw new ArgumentException("ExternalUserMapper handles LDAP and Windows providers only.", nameof(provider));

        var opts = _activeConfiguration?.Ldap ?? _options.CurrentValue;
        var groupSids = NormalizeGroupSids(ldapResult.GroupSids);
        var allowedGroupSids = NormalizeGroupSids(opts.AllowedGroupSids);
        // Defense in depth: the mapper is itself an authorization boundary. Never rely
        // solely on host-start validation because tooling and direct callers can construct
        // it independently. Missing allowlist configuration therefore fails closed.
        if (allowedGroupSids.Count == 0
            || !groupSids.Any(group => allowedGroupSids.Contains(group, StringComparer.OrdinalIgnoreCase)))
        {
            var revokedUserId = await RevokeKnownIdentityAccessAsync(
                ldapResult, groupSids, provider, ct);
            // A known identity is offboarded together with this audit row in one retryable
            // serializable transaction. An unknown identity has no state to mutate and
            // therefore keeps the simple refuse + audit-only path.
            if (revokedUserId is null)
            {
                await _audit.LogAsync(
                    AuditActions.UserDirectoryAccessRefused,
                    "User",
                    null,
                    AuditDetails.Json(
                        ("upn", ldapResult.Upn),
                        ("provider", provider.ToString()),
                        ("reason", "no_allowed_directory_group")),
                    ct);
            }
            return new ExternalUserMapping(
                ExternalUserMapResult.RefusedDirectoryAccess,
                null,
                "external identity is not assigned to an allowed directory group");
        }
        var role = GlobalRoleResolver.Resolve(groupSids, opts.GlobalRoleMappings);
        var groupSidsJson = JsonSerializer.Serialize(groupSids);
        var providerTag = provider == AuthProvider.Ldap ? "LDAP" : "WINDOWS";

        // 1. Existing JIT row — primary path on every login after the first.
        var authority = ExternalIdentity.ActiveDirectoryAuthority;
        var subject = ldapResult.Subject;
        if (string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("External identity subject must not be empty.", nameof(ldapResult));

        // Resolve the transport-independent identity first. LDAP and Negotiate both carry
        // the AD objectSid, so a person has one NodePilot user even when they switch paths.
        var canonicalIdentity = await _db.ExternalIdentities
            .Include(i => i.User)
            .SingleOrDefaultAsync(
                i => i.Authority == authority && i.Subject == subject,
                ct);

        // Rolling-upgrade compatibility. Old Windows rows stored the SID directly; old LDAP
        // rows stored objectGUID. LDAP returns that GUID as LegacyExternalId for an in-place
        // key upgrade. More than one legacy candidate is an existing data conflict and is
        // never resolved automatically.
        var legacyKey = provider == AuthProvider.Ldap
                        && !string.IsNullOrWhiteSpace(ldapResult.LegacyExternalId)
            ? ldapResult.LegacyExternalId!
            : subject;
        var legacyCandidates = await _db.Users
            .Where(u => u.Provider == provider && u.ExternalId == legacyKey)
            .Take(3)
            .ToListAsync(ct);

        if (legacyCandidates.Count > 1)
            return await RefuseIdentityConflictAsync(
                ldapResult, provider, null, "duplicate_legacy_external_identity", ct);

        var legacyUser = legacyCandidates.SingleOrDefault();
        if (canonicalIdentity is not null
            && legacyUser is not null
            && canonicalIdentity.UserId != legacyUser.Id)
        {
            return await RefuseIdentityConflictAsync(
                ldapResult, provider, canonicalIdentity.UserId,
                "canonical_and_legacy_identity_point_to_different_users", ct);
        }

        if (canonicalIdentity is not null)
        {
            if (canonicalIdentity.User.IsTombstoned)
                return await RefuseTombstonedAsync(ldapResult, provider, canonicalIdentity.UserId, ct);

            canonicalIdentity.LastSeenAt = DateTime.UtcNow;
            return await MapExistingAsync(
                canonicalIdentity.User, ldapResult, role, groupSids, groupSidsJson,
                providerTag, provider, ct);
        }

        if (legacyUser is not null)
        {
            if (legacyUser.IsTombstoned)
                return await RefuseTombstonedAsync(ldapResult, provider, legacyUser.Id, ct);

            var linkedIdentities = await _db.ExternalIdentities
                .Where(i => i.UserId == legacyUser.Id)
                .ToListAsync(ct);
            var legacyIdentity = linkedIdentities.SingleOrDefault(i =>
                i.Authority == ExternalIdentity.LegacyLdapAuthority
                && i.Subject == legacyKey);
            if (linkedIdentities.Any(i => i != legacyIdentity))
            {
                return await RefuseIdentityConflictAsync(
                    ldapResult, provider, legacyUser.Id,
                    "legacy_user_already_has_a_different_external_identity", ct);
            }

            if (legacyIdentity is null)
            {
                legacyIdentity = NewExternalIdentity(legacyUser.Id, subject);
                _db.ExternalIdentities.Add(legacyIdentity);
            }
            else
            {
                legacyIdentity.Authority = authority;
                legacyIdentity.Subject = subject;
                legacyIdentity.LastSeenAt = DateTime.UtcNow;
            }

            legacyUser.ExternalId = subject;
            return await MapExistingAsync(
                legacyUser, ldapResult, role, groupSids, groupSidsJson,
                providerTag, provider, ct,
                canonicalIdentityChanged: true);
        }

        // 2. Username-collision check. UPN as Username keeps local + LDAP namespaces
        // separated for typical setups; only the rare local-user-named-with-an-@ collides.
        var collision = await _db.Users
            .FirstOrDefaultAsync(u => u.Username == ldapResult.Upn, ct);
        if (collision is not null)
        {
            if (collision.IsTombstoned)
                return await RefuseTombstonedAsync(ldapResult, provider, collision.Id, ct);

            // Account history and privileges must never be transferred by a mutable username.
            await _audit.LogAsync(
                provider == AuthProvider.Ldap
                    ? AuditActions.UserLdapRefusedCollision
                    : AuditActions.UserWindowsRefusedCollision,
                "User",
                collision.Id,
                NodePilot.Core.Audit.AuditDetails.Json(
                    ("upn", ldapResult.Upn),
                    ("collidingUsername", collision.Username),
                    ("collidingProvider", collision.Provider.ToString()),
                    ("hasLocalPassword", !string.IsNullOrEmpty(collision.PasswordHash)),
                    ("autoLinkEnabled", false)),
                ct);
            _logger.LogWarning(
                "{Provider} login for UPN '{Upn}' refused: automatic identity merges are disabled",
                providerTag, ldapResult.Upn);
            return new ExternalUserMapping(
                ExternalUserMapResult.RefusedUsernameCollision, null,
                "username collision with an existing user");
        }

        // 3. Bootstrap-gate. Mirrors the local-login bootstrap rule (`!Users.Any()` requires
        //    a presented setup token) for external-auth: refuse to JIT a non-admin row when
        //    the DB is empty, because doing so would close the local bootstrap window
        //    forever (Users.Any() flips to true) and leave the instance with no admin path.
        //    Operators can bootstrap their first admin via SSO by mapping their AD-Group to
        //    Admin in GlobalRoleMappings — that branch is permitted here.
        var recoveryAccountExists = await BreakGlassAccountPolicy.ExistsAsync(_db, ct);
        if (!recoveryAccountExists)
        {
            await _audit.LogAsync(
                provider == AuthProvider.Ldap
                    ? AuditActions.UserLdapRefusedBootstrap
                    : AuditActions.UserWindowsRefusedBootstrap,
                "User",
                null,
                NodePilot.Core.Audit.AuditDetails.Json(
                    ("upn", ldapResult.Upn),
                    ("externalId", ldapResult.ExternalId),
                    ("resolvedRole", role.ToString()),
                    ("reason", "external_jit_blocked_until_breakglass_admin_exists")),
                ct);
            _logger.LogWarning(
                "{Provider} bootstrap refused for '{Upn}' — no local break-glass Admin exists yet (resolved external role {Role}). Bootstrap via X-Setup-Token first.",
                providerTag, ldapResult.Upn, role);
            return new ExternalUserMapping(
                ExternalUserMapResult.RefusedBootstrapNotAdmin, null,
                "external JIT blocked — bootstrap a local break-glass Admin with X-Setup-Token first");
        }

        // 4. Fresh JIT-provisioning.
        var newUser = new User
        {
            Id = Guid.NewGuid(),
            Username = ldapResult.Upn,
            PasswordHash = null,
            Provider = provider,
            ExternalId = subject,
            KnownGroupSidsJson = groupSidsJson,
            Role = role,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            PasswordChangedAt = DateTime.UtcNow,
            LastDirectorySyncAt = DateTime.UtcNow,
            DirectorySyncStatus = "Current",
        };
        _db.Users.Add(newUser);
        _db.ExternalIdentities.Add(NewExternalIdentity(newUser.Id, subject));
        await ReplaceDirectoryMembershipsAsync(newUser.Id, groupSids, ct);

        // Optional Default-Folder grant on Root. Off by default — admins explicitly opt in
        // via Authentication:Ldap:JitUserDefaultRootRole. Admin role doesn't get a grant
        // (global Admin bypasses folder ACLs).
        if (opts.JitUserDefaultRootRole is { } defaultRole && role != UserRole.Admin)
        {
            _db.SharedFolderPermissions.Add(new SharedFolderPermission
            {
                Id = Guid.NewGuid(),
                FolderId = SharedWorkflowFolder.RootFolderId,
                PrincipalType = FolderPrincipalType.User,
                PrincipalKey = newUser.Id.ToString("D"),
                Role = defaultRole,
                GrantedAt = DateTime.UtcNow,
                GrantedByUserId = null, // system default — UI surfaces accordingly
            });
        }

        try
        {
            await SaveMutationWithAuditAsync(
                newUser,
                provider == AuthProvider.Ldap
                    ? AuditActions.UserLdapJitCreated
                    : AuditActions.UserWindowsJitCreated,
                BuildAuditJson(ldapResult, null, role, newGroupSids: groupSids),
                ct);
            return new ExternalUserMapping(ExternalUserMapResult.Mapped, newUser, null);
        }
        catch (DbUpdateException ex)
        {
            // The in-process semaphore cannot serialize JIT across HA nodes. The unique
            // (Authority, Subject) index is the cross-node arbiter; a loser re-reads the
            // winner and continues only when it is the exact same canonical AD identity.
            _logger.LogWarning(ex,
                "Concurrent {Provider} JIT provisioning detected for subject {Subject}",
                providerTag, subject);
            _db.ChangeTracker.Clear();
            var winner = await _db.ExternalIdentities
                .Include(i => i.User)
                .SingleOrDefaultAsync(i => i.Authority == authority && i.Subject == subject, ct);
            if (winner?.User is { IsActive: true, IsTombstoned: false } winnerUser)
            {
                return await MapExistingAsync(
                    winnerUser, ldapResult, role, groupSids, groupSidsJson,
                    providerTag, provider, ct);
            }
            return await RefuseIdentityConflictAsync(
                ldapResult, provider, winner?.UserId,
                "concurrent_jit_identity_or_username_conflict", ct);
        }
    }

    private async Task<ExternalUserMapping> MapExistingAsync(
        User existing,
        LdapAuthResult ldapResult,
        UserRole resolvedRole,
        IReadOnlyList<string> newGroupSids,
        string newGroupSidsJson,
        string providerTag,
        AuthProvider provider,
        CancellationToken ct,
        bool canonicalIdentityChanged = false)
    {
        await using var localGate = await AdminAccountMutationGate.EnterLocalAsync(ct);
        var strategy = _db.Database.CreateExecutionStrategy();
        IReadOnlyList<Guid> committedExecutionIds = [];
        AuditLogEntry? committedAudit = null;
        Guid? invalidateCacheUserId = null;
        var result = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            committedExecutionIds = [];
            committedAudit = null;
            invalidateCacheUserId = null;
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, ct);
            await AdminAccountMutationGate.AcquireTransactionLockAsync(_db, ct);

            // Never reuse the entity captured before waiting for the gate. A retry may run
            // on a different connection, and a concurrent admin mutation may have changed
            // the invariant-relevant role or account state in the meantime.
            var current = await _db.Users.SingleOrDefaultAsync(
                candidate => candidate.Id == existing.Id, ct);
            if (current is null)
            {
                var missing = await RefuseIdentityConflictAsync(
                    ldapResult, provider, existing.Id,
                    "mapped_user_disappeared_during_authorization_update", ct);
                await transaction.CommitAsync(ct);
                return missing;
            }

            var identity = await _db.ExternalIdentities.SingleOrDefaultAsync(candidate =>
                candidate.Authority == ExternalIdentity.ActiveDirectoryAuthority
                && candidate.Subject == ldapResult.Subject, ct);
            if (canonicalIdentityChanged)
            {
                var legacyKey = string.IsNullOrWhiteSpace(ldapResult.LegacyExternalId)
                    ? ldapResult.Subject
                    : ldapResult.LegacyExternalId;
                identity ??= await _db.ExternalIdentities.SingleOrDefaultAsync(candidate =>
                    candidate.UserId == current.Id
                    && candidate.Authority == ExternalIdentity.LegacyLdapAuthority
                    && candidate.Subject == legacyKey, ct);
                if (identity is null)
                {
                    identity = NewExternalIdentity(current.Id, ldapResult.Subject);
                    _db.ExternalIdentities.Add(identity);
                }
                else
                {
                    identity.Authority = ExternalIdentity.ActiveDirectoryAuthority;
                    identity.Subject = ldapResult.Subject;
                }
                current.ExternalId = ldapResult.Subject;
            }
            if (identity is not null)
                identity.LastSeenAt = DateTime.UtcNow;

            var coreResult = await MapExistingCoreAsync(
                current, ldapResult, resolvedRole, newGroupSids, newGroupSidsJson,
                providerTag, provider, ct, canonicalIdentityChanged);
            await transaction.CommitAsync(ct);
            committedExecutionIds = coreResult.ExecutionIds;
            committedAudit = coreResult.Audit;
            if (coreResult.InvalidateCache)
                invalidateCacheUserId = current.Id;
            return coreResult.Mapping;
        });

        if (invalidateCacheUserId is { } userId)
            UserSessionInvalidation.InvalidateUserStateCache(_userStateCache, userId);
        if (committedAudit is not null)
            AuditEventForwarder.ForwardCommitted(_logger, committedAudit);
        await SignalCommittedExecutionCancellationsAsync(
            committedExecutionIds, "directory-authorization-change");
        return result;
    }

    private async Task<(
        ExternalUserMapping Mapping,
        IReadOnlyList<Guid> ExecutionIds,
        AuditLogEntry Audit,
        bool InvalidateCache)> MapExistingCoreAsync(
        User existing,
        LdapAuthResult ldapResult,
        UserRole resolvedRole,
        IReadOnlyList<string> newGroupSids,
        string newGroupSidsJson,
        string providerTag,
        AuthProvider provider,
        CancellationToken ct,
        bool canonicalIdentityChanged = false)
    {
            var oldRole = existing.Role;
            var oldGroupSids = ParseGroupSids(existing.KnownGroupSidsJson);

            if (oldRole == UserRole.Admin && existing.IsActive && resolvedRole != UserRole.Admin)
            {
                var otherActiveAdmins = await _db.Users.CountAsync(
                    u => u.Id != existing.Id && u.Role == UserRole.Admin && u.IsActive,
                    ct);
                if (otherActiveAdmins == 0)
                {
                    // Keep the recoverable database invariant, but record the current
                    // directory groups and invalidate stale Admin sessions. The caller
                    // refuses this login, so the preserved role is never minted into a JWT.
                    existing.KnownGroupSidsJson = newGroupSidsJson;
                    UserSessionInvalidation.BumpSecurityStamp(existing);
                    await ReplaceDirectoryMembershipsAsync(existing.Id, newGroupSids, ct);
                    var executionIds = await RevokeSessionsAndCancelExecutionsAsync(
                        existing.Id, DateTime.UtcNow, ct);

                    var refusedAudit = _auditStager.Build(
                        provider == AuthProvider.Ldap
                            ? AuditActions.UserLdapRefusedLastAdmin
                            : AuditActions.UserWindowsRefusedLastAdmin,
                        ResolveAuditActor(),
                        "User",
                        existing.Id,
                        BuildAuditJson(
                            ldapResult, oldRole, resolvedRole, oldGroupSids, newGroupSids,
                            reason: "last_active_admin_demotion"));
                    _db.AuditLog.Add(refusedAudit);
                    await _db.SaveChangesAsync(ct);

                    _logger.LogWarning(
                        "{Provider} login for '{Upn}' refused: directory mapping would demote the last active Admin from {OldRole} to {NewRole}",
                        providerTag, ldapResult.Upn, oldRole, resolvedRole);
                    return (new ExternalUserMapping(
                            ExternalUserMapResult.RefusedLastActiveAdmin,
                            null,
                            "directory mapping would demote the last active admin"),
                        executionIds,
                        refusedAudit,
                        InvalidateCache: true);
                }
            }

            // Don't touch IsActive: an administrator's explicit deactivation remains in
            // force even after successful directory authentication.
            var roleChanged = oldRole != resolvedRole;
            var groupsChanged = !oldGroupSids.SequenceEqual(newGroupSids, StringComparer.OrdinalIgnoreCase);
            var authorizationReduced = resolvedRole < oldRole
                || oldGroupSids.Except(newGroupSids, StringComparer.OrdinalIgnoreCase).Any();
            var identityChanged = canonicalIdentityChanged;
            if (canonicalIdentityChanged)
            {
                // Reload above may have restored the legacy objectGUID compatibility value.
                // Project the canonical SID only after entering the mutation gate.
                existing.ExternalId = ldapResult.Subject;
            }
            existing.Role = resolvedRole;
            existing.KnownGroupSidsJson = newGroupSidsJson;
            existing.LastDirectorySyncAt = DateTime.UtcNow;
            existing.DirectorySyncStatus = "Current";
            await ReplaceDirectoryMembershipsAsync(existing.Id, newGroupSids, ct);
            if (roleChanged || groupsChanged || identityChanged)
                UserSessionInvalidation.BumpSecurityStamp(existing);
            IReadOnlyList<Guid> executionIdsToSignal = [];
            if (authorizationReduced)
                executionIdsToSignal = await RevokeSessionsAndCancelExecutionsAsync(
                    existing.Id, DateTime.UtcNow, ct);

            var audit = _auditStager.Build(
                provider == AuthProvider.Ldap
                    ? AuditActions.UserLdapJitUpdated
                    : AuditActions.UserWindowsJitUpdated,
                ResolveAuditActor(),
                "User",
                existing.Id,
                BuildAuditJson(ldapResult, oldRole, resolvedRole, oldGroupSids, newGroupSids));
            _db.AuditLog.Add(audit);
            await _db.SaveChangesAsync(ct);

        return (new ExternalUserMapping(ExternalUserMapResult.Mapped, existing, null),
            executionIdsToSignal,
            audit,
            roleChanged || groupsChanged || identityChanged);
    }

    private async Task<Guid?> RevokeKnownIdentityAccessAsync(
        LdapAuthResult ldapResult,
        IReadOnlyList<string> observedGroupSids,
        AuthProvider provider,
        CancellationToken ct)
    {
        // Avoid taking the global Admin invariant gate for arbitrary rejected credentials.
        // This read is only a routing hint: the authoritative identity/user state is
        // reloaded after the transaction lock has been acquired below.
        var knownIdentity = await _db.ExternalIdentities.AsNoTracking().AnyAsync(candidate =>
            candidate.Authority == ExternalIdentity.ActiveDirectoryAuthority
            && candidate.Subject == ldapResult.Subject
            && !candidate.User.IsTombstoned, ct);
        if (!knownIdentity)
            return null;

        await using var localGate = await AdminAccountMutationGate.EnterLocalAsync(ct);
        var strategy = _db.Database.CreateExecutionStrategy();
        AuditLogEntry? committedAudit = null;
        IReadOnlyList<Guid> committedExecutionIds = [];
        var userId = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            committedAudit = null;
            committedExecutionIds = [];
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, ct);
            await AdminAccountMutationGate.AcquireTransactionLockAsync(_db, ct);

            var identity = await _db.ExternalIdentities
                .Include(candidate => candidate.User)
                .SingleOrDefaultAsync(candidate =>
                    candidate.Authority == ExternalIdentity.ActiveDirectoryAuthority
                    && candidate.Subject == ldapResult.Subject, ct);
            if (identity?.User is not { IsTombstoned: false } user)
            {
                await transaction.RollbackAsync(ct);
                return (Guid?)null;
            }

            var now = DateTime.UtcNow;
            var oldGroups = ParseGroupSids(user.KnownGroupSidsJson);
            var stateChanged = user.IsActive
                || user.DirectorySyncStatus != "AccessRevoked"
                || !oldGroups.SequenceEqual(observedGroupSids, StringComparer.OrdinalIgnoreCase);
            user.IsActive = false;
            user.LastDirectorySyncAt = now;
            user.DirectorySyncStatus = "AccessRevoked";
            user.KnownGroupSidsJson = JsonSerializer.Serialize(observedGroupSids);
            identity.LastSeenAt = now;
            await ReplaceDirectoryMembershipsAsync(user.Id, observedGroupSids, ct);
            if (stateChanged)
                UserSessionInvalidation.BumpSecurityStamp(user);

            var sessions = await _db.AuthSessions
                .Where(session => session.UserId == user.Id && session.RevokedAt == null)
                .ToListAsync(ct);
            foreach (var session in sessions)
                session.RevokedAt = now;

            // Persist the durable cancellation before touching the in-memory engine. A
            // Pending claim can no longer succeed after commit; an already Running row is
            // included in the returned ids and signalled immediately after commit below.
            var executionIds = await ExternalExecutionCancellation.CancelAsync(
                _db,
                [user.Id],
                now,
                "directory-authorization-change",
                "Execution cancelled because its directory principal authorization changed.",
                ct);

            var audit = _auditStager.Build(
                AuditActions.UserDirectoryAccessRefused,
                ResolveAuditActor(),
                "User",
                user.Id,
                AuditDetails.Json(
                    ("upn", ldapResult.Upn),
                    ("provider", provider.ToString()),
                    ("reason", "no_allowed_directory_group")));
            _db.AuditLog.Add(audit);
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            committedAudit = audit;
            committedExecutionIds = executionIds;
            return (Guid?)user.Id;
        });

        if (userId is null)
            return null;

        UserSessionInvalidation.InvalidateUserStateCache(_userStateCache, userId.Value);
        if (committedAudit is not null)
            AuditEventForwarder.ForwardCommitted(_logger, committedAudit);
        await SignalCommittedExecutionCancellationsAsync(
            committedExecutionIds, "directory-authorization-change");
        _logger.LogWarning(
            "{Provider} authorization snapshot immediately deprovisioned user {UserId}: no allowed directory group remains",
            provider, userId.Value);
        return userId.Value;
    }

    private async Task SignalCommittedExecutionCancellationsAsync(
        IReadOnlyCollection<Guid> executionIds,
        string reason)
    {
        if (_workflowEngine is null || executionIds.Count == 0) return;
        // The security mutation is already committed. RequestAborted must not suppress
        // the corresponding in-memory cancellation when the client disconnects while the
        // response is being written. Bound this independent post-commit work globally.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await ExternalExecutionCancellation.SignalAfterCommitAsync(
                _workflowEngine, executionIds, reason, timeout.Token, _logger);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Timed out signalling committed directory offboarding after 5 seconds");
        }
    }

    private AuditActor ResolveAuditActor()
    {
        var context = _httpContextAccessor?.HttpContext;
        if (context is null) return AuditActor.System;
        var userId = Guid.TryParse(
            context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsed)
            ? parsed
            : (Guid?)null;
        return new AuditActor(
            userId,
            context.User.FindFirstValue(ClaimTypes.Name),
            context.Connection.RemoteIpAddress?.ToString());
    }

    private async Task<IReadOnlyList<Guid>> RevokeSessionsAndCancelExecutionsAsync(
        Guid userId,
        DateTime now,
        CancellationToken ct)
    {
        var sessions = await _db.AuthSessions
            .Where(session => session.UserId == userId && session.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var session in sessions)
            session.RevokedAt = now;

        return await ExternalExecutionCancellation.CancelAsync(
            _db,
            [userId],
            now,
            "directory-authorization-change",
            "Execution cancelled because its directory principal authorization changed.",
            ct);
    }

    private static ExternalIdentity NewExternalIdentity(Guid userId, string subject)
    {
        var now = DateTime.UtcNow;
        return new ExternalIdentity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Authority = ExternalIdentity.ActiveDirectoryAuthority,
            Subject = subject,
            CreatedAt = now,
            LastSeenAt = now,
        };
    }

    private async Task<ExternalUserMapping> RefuseIdentityConflictAsync(
        LdapAuthResult ldapResult,
        AuthProvider provider,
        Guid? userId,
        string reason,
        CancellationToken ct)
    {
        await _audit.LogAsync(
            provider == AuthProvider.Ldap
                ? AuditActions.UserLdapRefusedCollision
                : AuditActions.UserWindowsRefusedCollision,
            "User",
            userId,
            NodePilot.Core.Audit.AuditDetails.Json(
                ("upn", ldapResult.Upn),
                ("authority", ExternalIdentity.ActiveDirectoryAuthority),
                ("subject", ldapResult.Subject),
                ("legacyExternalId", ldapResult.LegacyExternalId),
                ("reason", reason)),
            ct);
        _logger.LogError(
            "{Provider} login for '{Upn}' refused due to external identity conflict ({Reason})",
            provider, ldapResult.Upn, reason);
        return new ExternalUserMapping(
            ExternalUserMapResult.RefusedIdentityConflict,
            null,
            "external identity conflict requires administrator resolution");
    }

    private async Task<ExternalUserMapping> RefuseTombstonedAsync(
        LdapAuthResult ldapResult,
        AuthProvider provider,
        Guid userId,
        CancellationToken ct)
    {
        await _audit.LogAsync(
            provider == AuthProvider.Ldap
                ? AuditActions.UserLdapRefusedCollision
                : AuditActions.UserWindowsRefusedCollision,
            "User",
            userId,
            NodePilot.Core.Audit.AuditDetails.Json(
                ("upn", ldapResult.Upn),
                ("authority", ExternalIdentity.ActiveDirectoryAuthority),
                ("subject", ldapResult.Subject),
                ("reason", "external_identity_tombstoned")),
            ct);
        return new ExternalUserMapping(
            ExternalUserMapResult.RefusedTombstoned,
            null,
            "external identity is tombstoned");
    }

    /// <summary>
    /// AuditWriter shares this DbContext in production. Invoking it before the mapper's
    /// SaveChanges persists the tracked User mutation and AuditLog row together in EF's
    /// implicit transaction. Capturing test writers do not persist, so the fallback
    /// SaveChanges remains their observable mutation boundary.
    /// </summary>
    private async Task SaveMutationWithAuditAsync(
        User user,
        string action,
        string details,
        CancellationToken ct)
    {
        // Production AuditWriter stages the audit row on this DbContext and its single
        // SaveChanges commits every already-tracked identity/user/membership mutation in
        // the provider's implicit transaction. Avoid a user transaction here: SQL Server
        // and Npgsql retry strategies can safely replay SaveChanges, whereas replaying a
        // transaction after entities became Unchanged at CommitAsync is not retry-safe.
        await _audit.LogAsync(action, "User", user.Id, details, ct);
        // Capturing/no-op test writers do not persist. In that case the mutation remains
        // tracked and this is its observable commit boundary; in production it is a no-op.
        if (_db.ChangeTracker.HasChanges())
            await _db.SaveChangesAsync(ct);
    }

    private async Task ReplaceDirectoryMembershipsAsync(
        Guid userId,
        IReadOnlyCollection<string> groupKeys,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var desired = groupKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existing = await _db.DirectoryMemberships
            .Where(m => m.UserId == userId
                     && m.Authority == ExternalIdentity.ActiveDirectoryAuthority)
            .ToListAsync(ct);

        foreach (var membership in existing)
        {
            if (!desired.Contains(membership.GroupKey))
                _db.DirectoryMemberships.Remove(membership);
            else
                membership.LastSeenAt = now;
        }

        var existingKeys = existing.Select(m => m.GroupKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var groupKey in desired.Where(key => !existingKeys.Contains(key)))
        {
            _db.DirectoryMemberships.Add(new DirectoryMembership
            {
                UserId = userId,
                Authority = ExternalIdentity.ActiveDirectoryAuthority,
                GroupKey = groupKey,
                LastSeenAt = now,
            });
        }
    }

    private static IReadOnlyList<string> NormalizeGroupSids(IEnumerable<string> groupSids) =>
        groupSids
            .Where(sid => !string.IsNullOrWhiteSpace(sid))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(sid => sid, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> ParseGroupSids(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            return NormalizeGroupSids(JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>());
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static string GroupSidsFingerprint(IReadOnlyList<string> groupSids)
    {
        var canonical = string.Join('\n', groupSids);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string BuildAuditJson(
        LdapAuthResult ldap,
        UserRole? oldRole,
        UserRole newRole,
        IReadOnlyList<string>? oldGroupSids = null,
        IReadOnlyList<string>? newGroupSids = null,
        string? reason = null)
    {
        oldGroupSids ??= Array.Empty<string>();
        newGroupSids ??= NormalizeGroupSids(ldap.GroupSids);
        var allAdded = newGroupSids.Except(oldGroupSids, StringComparer.OrdinalIgnoreCase).ToArray();
        var allRemoved = oldGroupSids.Except(newGroupSids, StringComparer.OrdinalIgnoreCase).ToArray();
        var added = allAdded.Take(MaxAuditedGroupSidDeltaEntries).ToArray();
        var removed = allRemoved.Take(MaxAuditedGroupSidDeltaEntries).ToArray();

        return NodePilot.Core.Audit.AuditDetails.Json(
            ("upn", ldap.Upn),
            ("externalId", ldap.ExternalId),
            ("displayName", ldap.DisplayName),
            ("oldRole", oldRole?.ToString()),
            ("newRole", newRole.ToString()),
            ("oldGroupSidsCount", oldGroupSids.Count),
            ("newGroupSidsCount", newGroupSids.Count),
            ("oldGroupSidsHash", GroupSidsFingerprint(oldGroupSids)),
            ("newGroupSidsHash", GroupSidsFingerprint(newGroupSids)),
            ("addedGroupSidsCount", allAdded.Length),
            ("removedGroupSidsCount", allRemoved.Length),
            ("addedGroupSids", added),
            ("removedGroupSids", removed),
            ("groupSidDeltaTruncated",
                allAdded.Length > MaxAuditedGroupSidDeltaEntries
                || allRemoved.Length > MaxAuditedGroupSidDeltaEntries),
            ("reason", reason));
    }
}
