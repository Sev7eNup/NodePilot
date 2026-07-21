using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Configuration;
using NodePilot.Api.Security;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Security;

namespace NodePilot.Api.Services.Backup;

/// <summary>
/// Restores a <c>nodepilot-system-backup/v1</c> archive (ADR 0001 Phase 2). Preview is read-only
/// and passphrase-optional; restore requires the passphrase, verifies the whole-file MAC, validates
/// that every hard reference resolves (K12), then writes all DB sections in one transaction in
/// dependency order (K4) while building source→target id-maps (K3). Workflow-definition GUID
/// references are remapped (K13), user sessions are invalidated on overwrite (K16), the last active
/// admin is protected (K11), and settings are applied last, outside the transaction (K8).
/// </summary>
public sealed class BackupRestoreService(
    NodePilotDbContext db,
    ISecretProtector atRest,
    RuntimeOverridesWriter overrides,
    ILogger<BackupRestoreService> logger)
{
    // ---- Preview ------------------------------------------------------------

    public async Task<BackupPreviewResult> PreviewAsync(byte[] content, string? passphrase, CancellationToken ct)
    {
        var reader = BackupFileReader.Parse(content);
        var warnings = new List<string>();

        var protector = string.IsNullOrEmpty(passphrase) ? null : reader.TryUnlock(passphrase);
        var integrityVerified = false;
        if (!string.IsNullOrEmpty(passphrase))
        {
            if (protector is null) warnings.Add("Passphrase is incorrect — integrity could not be verified.");
            else if (!reader.VerifyMac(protector)) warnings.Add("Whole-file MAC does not match — the file may be corrupt or tampered.");
            else integrityVerified = true;
        }
        else
        {
            warnings.Add("No passphrase supplied — integrity unverified; secret values are not compared (K10).");
        }

        var sections = new List<BackupPreviewSection>();
        foreach (var key in RestoreOrder.Concat([BackupSections.Settings]))
        {
            if (reader.Sections[key] is null) continue;
            sections.Add(await PreviewSectionAsync(key, reader, ct));
        }

        return new BackupPreviewResult(integrityVerified, reader.AppVersion, sections, warnings);
    }

    private async Task<BackupPreviewSection> PreviewSectionAsync(string key, BackupFileReader reader, CancellationToken ct)
    {
        switch (key)
        {
            case BackupSections.Users:
            {
                var names = await db.Users.Select(u => u.Username).ToListAsync(ct);
                return DiffByName(key, Items(reader, key), "username", names);
            }
            case BackupSections.Credentials:
            {
                var names = await db.Credentials.Select(c => c.Name).ToListAsync(ct);
                return DiffByName(key, Items(reader, key), "name", names);
            }
            case BackupSections.Machines:
            {
                var names = await db.ManagedMachines.Select(m => m.Name).ToListAsync(ct);
                return DiffByName(key, Items(reader, key), "name", names);
            }
            case BackupSections.GlobalVariables:
            {
                var names = await db.GlobalVariables.Select(v => v.Name).ToListAsync(ct);
                return DiffByName(key, Items(reader, key), "name", names);
            }
            case BackupSections.GlobalVariableFolders:
            {
                var paths = await db.GlobalVariableFolders.Select(f => f.Path).ToListAsync(ct);
                var structure = (reader.Sections[key] as JsonObject)?["structure"] as JsonArray ?? [];
                return DiffByName(key, structure, "path", paths);
            }
            case BackupSections.CustomActivities:
            {
                var keys = await db.CustomActivityDefinitions.Where(d => !d.IsDeleted).Select(d => d.Key).ToListAsync(ct);
                return DiffByName(key, Items(reader, key), "key", keys);
            }
            case BackupSections.Workflows:
            {
                var names = await db.Workflows.Select(w => w.Name).ToListAsync(ct);
                return DiffByName(key, Items(reader, key), "name", names);
            }
            case BackupSections.Folders:
            {
                var paths = await db.SharedWorkflowFolders.Select(f => f.Path).ToListAsync(ct);
                var structure = (reader.Sections[key] as JsonObject)?["structure"] as JsonArray ?? [];
                return DiffByName(key, structure, "path", paths);
            }
            case BackupSections.Settings:
            {
                var obj = (reader.Sections[key] as JsonObject)?["runtimeJson"] as JsonObject;
                var count = obj?.Count ?? 0;
                return new BackupPreviewSection(key, count, count, 0);
            }
            default:
                return new BackupPreviewSection(key, 0, 0, 0);
        }
    }

    private static BackupPreviewSection DiffByName(string key, JsonArray items, string nameField, IEnumerable<string> existing)
    {
        var existingSet = new HashSet<string>(existing, StringComparer.Ordinal);
        var conflicts = 0;
        foreach (var item in items)
        {
            var name = item?[nameField]?.GetValue<string>();
            if (name is not null && existingSet.Contains(name)) conflicts++;
        }
        return new BackupPreviewSection(key, items.Count, items.Count - conflicts, conflicts);
    }

    // ---- Restore ------------------------------------------------------------

    // Dependency order (K4): users → folder-structure → credentials → machines → globals →
    // workflows → folder-grants. Settings (K8) are applied after, outside the transaction.
    private static readonly string[] RestoreOrder =
    [
        BackupSections.Users, BackupSections.Folders, BackupSections.Credentials,
        BackupSections.Machines, BackupSections.GlobalVariableFolders, BackupSections.GlobalVariables,
        BackupSections.CustomActivities, BackupSections.Workflows, BackupSections.Alerting,
    ];

    public async Task<BackupRestoreResult> RestoreAsync(
        byte[] content, string passphrase, IReadOnlyDictionary<string, RestoreConflictPolicy> policies, CancellationToken ct)
    {
        var reader = BackupFileReader.Parse(content);
        var protector = reader.TryUnlock(passphrase)
            ?? throw new BackupRestoreException("Passphrase is incorrect.");
        if (!reader.VerifyMac(protector))
            throw new BackupRestoreException("Whole-file MAC does not match — the backup is corrupt or has been tampered with. Restore aborted.");

        // Join every other path that can reduce the active-Admin set. Holding the gate
        // across guard + transaction prevents concurrent individually-safe mutations
        // from collectively removing every active Admin.
        var restoresUsers = reader.Sections[BackupSections.Users] is not null
            && Items(reader, BackupSections.Users).Any();
        var existingUserCount = restoresUsers ? await db.Users.CountAsync(ct) : 0;
        var recoveryExistedBefore = restoresUsers
            && await BreakGlassAccountPolicy.ExistsAsync(db, ct);
        var enforceRecoveryAfterRestore = restoresUsers
            && (recoveryExistedBefore || existingUserCount == 0);
        await using var adminMutation = restoresUsers
            ? await AdminAccountMutationGate.EnterLocalAsync(ct)
            : null;

        // The whole DB restore runs inside the provider's execution strategy. Postgres configures
        // a retrying strategy (NpgsqlRetryingExecutionStrategy), which forbids a user-initiated
        // BeginTransaction unless it's wrapped here so the transaction can be replayed atomically
        // on a transient failure. Each attempt rebuilds the state and clears the change tracker so
        // a retry starts clean. SQLite (tests) returns a non-retrying strategy → runs once.
        var results = new List<SectionRestoreResult>();
        var warnings = new List<string>();
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            results.Clear();
            var ctx = new RestoreState(reader, protector, policies);
            await LoadExistingAsync(ctx, ct);
            await ValidateReferencesAsync(ctx, ct); // K12 — abort before any write

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (restoresUsers)
                await AdminAccountMutationGate.AcquireTransactionLockAsync(db, ct);
            try
            {
                if (reader.Sections[BackupSections.Users] is not null) results.Add(await RestoreUsersAsync(ctx, ct));
                if (restoresUsers
                    && !await db.Users.AnyAsync(
                        user => user.Role == UserRole.Admin
                             && user.IsActive
                             && !user.IsTombstoned,
                        ct))
                    throw new BackupRestoreException(
                        "Restore aborted: the user set would leave no active Admin.");
                // K11 — BreakGlassOnly must always retain an independently recoverable admin.
                if (enforceRecoveryAfterRestore
                    && !await BreakGlassAccountPolicy.ExistsAsync(db, ct))
                    throw new BackupRestoreException(
                        "Restore aborted: the user set would leave no active Admin with a local break-glass credential.");

                if (reader.Sections[BackupSections.Folders] is not null) results.Add(await RestoreFoldersAsync(ctx, ct));
                if (reader.Sections[BackupSections.Credentials] is not null) results.Add(await RestoreCredentialsAsync(ctx, ct));
                if (reader.Sections[BackupSections.Machines] is not null) results.Add(await RestoreMachinesAsync(ctx, ct));
                if (reader.Sections[BackupSections.GlobalVariableFolders] is not null) results.Add(await RestoreGlobalFoldersAsync(ctx, ct));
                if (reader.Sections[BackupSections.GlobalVariables] is not null) results.Add(await RestoreGlobalsAsync(ctx, ct));
                if (reader.Sections[BackupSections.CustomActivities] is not null) results.Add(await RestoreCustomActivitiesAsync(ctx, ct));
                if (reader.Sections[BackupSections.Workflows] is not null) results.Add(await RestoreWorkflowsAsync(ctx, ct));
                if (reader.Sections[BackupSections.Alerting] is not null) results.Add(await RestoreAlertingAsync(ctx, ct));
                if (reader.Sections[BackupSections.Folders] is not null) results.Add(await RestoreGrantsAsync(ctx, ct));

                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
            warnings.Clear();
            warnings.AddRange(ctx.Warnings);
        });

        // K8 — settings are file-based (RuntimeOverridesWriter), not part of the DB transaction.
        SettingsRestoreResult? settings = null;
        if (reader.Sections[BackupSections.Settings] is not null)
            settings = RestoreSettings(reader, protector, warnings);

        return new BackupRestoreResult(results, settings, warnings);
    }

    // ---- per-section restore ----

    private async Task<SectionRestoreResult> RestoreUsersAsync(RestoreState s, CancellationToken ct)
    {
        var policy = s.Policy(BackupSections.Users);
        int created = 0, overwritten = 0, skipped = 0, renamed = 0;
        var takenNames = new HashSet<string>(s.Users.Keys, StringComparer.Ordinal);

        foreach (var item in Items(s.Reader, BackupSections.Users))
        {
            var sourceId = Gid(item!["sourceId"]);
            var username = item["username"]!.GetValue<string>();
            var role = Enum.Parse<UserRole>(item["role"]!.GetValue<string>());
            var isActive = item["isActive"]?.GetValue<bool>() ?? true;
            var isBreakGlass = item["isBreakGlass"]?.GetValue<bool>() ?? false;
            var isTombstoned = item["isTombstoned"]?.GetValue<bool>() ?? false;
            var provider = Enum.Parse<AuthProvider>(item["provider"]?.GetValue<string>() ?? "Local");
            var externalId = item["externalId"]?.GetValue<string>();
            var groupSids = item["knownGroupSidsJson"]?.GetValue<string>();
            var securityStamp = item["securityStamp"]?.GetValue<int>() ?? 0;
            var passwordChangedAt = DateTime.TryParse(
                item["passwordChangedAt"]?.GetValue<string>(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var pca) ? pca : DateTime.UtcNow;
            var lastDirectorySyncAt = DateTime.TryParse(
                item["lastDirectorySyncAt"]?.GetValue<string>(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var syncAt)
                ? syncAt
                : (DateTime?)null;
            var directorySyncStatus = item["directorySyncStatus"]?.GetValue<string>();
            var passwordHash = DecryptField(item["passwordHash"], s.Protector);

            var incomingIdentityKeys = IncomingIdentityKeys(item, provider, externalId);
            s.UsersById.TryGetValue(sourceId, out var sourceIdMatch);
            if (sourceIdMatch is not null
                && !IsSameRestoredIdentity(sourceIdMatch, sourceId, provider, externalId, incomingIdentityKeys))
            {
                throw new BackupRestoreException(
                    $"User restore refused: source id {sourceId} belongs to a different target identity.");
            }

            var identityMatches = incomingIdentityKeys.Count == 0
                ? []
                : s.UsersById.Values
                    .Where(candidate => candidate.ExternalIdentities.Any(identity =>
                        incomingIdentityKeys.Contains((identity.Authority, identity.Subject))))
                    .DistinctBy(candidate => candidate.Id)
                    .ToList();
            if (identityMatches.Count > 1)
                throw new BackupRestoreException(
                    $"User restore refused: backup identity for '{username}' is already ambiguous in the target database.");
            var existing = sourceIdMatch ?? identityMatches.SingleOrDefault();
            var hasUsernameCollision = s.Users.TryGetValue(username, out var usernameMatch)
                && usernameMatch.Id != existing?.Id;

            if (existing is not null)
            {
                if (policy == RestoreConflictPolicy.Skip) { s.UserMap[sourceId] = existing.Id; skipped++; continue; }
                if (policy == RestoreConflictPolicy.Overwrite)
                {
                    // K16 — bump SecurityStamp (invalidate live sessions) on a security-relevant change.
                    if (existing.Role != role || existing.IsActive != isActive
                        || existing.IsBreakGlass != isBreakGlass || existing.IsTombstoned != isTombstoned
                        || existing.PasswordHash != passwordHash)
                        existing.SecurityStamp += 1;
                    if (existing.PasswordHash != passwordHash) existing.PasswordChangedAt = DateTime.UtcNow;
                    existing.Role = role; existing.IsActive = isActive; existing.PasswordHash = passwordHash;
                    existing.IsBreakGlass = isBreakGlass; existing.IsTombstoned = isTombstoned;
                    existing.LastDirectorySyncAt = lastDirectorySyncAt; existing.DirectorySyncStatus = directorySyncStatus;
                    existing.Provider = provider; existing.ExternalId = externalId; existing.KnownGroupSidsJson = groupSids;
                    await RestoreExternalIdentitiesAsync(item, existing.Id, replaceExisting: true, ct);
                    await RestoreDirectoryMembershipsAsync(item, existing.Id, replaceExisting: true, ct);
                    s.UserMap[sourceId] = existing.Id; overwritten++; continue;
                }
                // An exact source-id or external-identity match is the same principal.
                // Rename must not clone it and duplicate its immutable identity.
                s.UserMap[sourceId] = existing.Id;
                skipped++;
                continue;
            }
            else if (hasUsernameCollision)
            {
                if (policy != RestoreConflictPolicy.Rename)
                {
                    throw new BackupRestoreException(
                        $"User restore refused: username '{username}' belongs to a different identity. " +
                        "Use Rename or resolve the identity conflict explicitly; users are never merged by username.");
                }
                username = UniqueName(username, takenNames);
                renamed++;
            }
            else created++;

            takenNames.Add(username);
            var id = s.ExistingUserIds.Contains(sourceId) ? Guid.NewGuid() : sourceId;
            var user = new User
            {
                Id = id, Username = username, Role = role, IsActive = isActive, Provider = provider,
                ExternalId = externalId, KnownGroupSidsJson = groupSids, PasswordHash = passwordHash,
                SecurityStamp = securityStamp, PasswordChangedAt = passwordChangedAt,
                IsBreakGlass = isBreakGlass, IsTombstoned = isTombstoned,
                LastDirectorySyncAt = lastDirectorySyncAt, DirectorySyncStatus = directorySyncStatus,
            };
            db.Users.Add(user);
            await RestoreExternalIdentitiesAsync(item, user.Id, replaceExisting: false, ct);
            await RestoreDirectoryMembershipsAsync(item, user.Id, replaceExisting: false, ct);
            s.Users[username] = user; s.UsersById[id] = user;
            s.ExistingUserIds.Add(id); s.UserMap[sourceId] = id;
        }
        await db.SaveChangesAsync(ct);
        return new SectionRestoreResult(BackupSections.Users, created, overwritten, skipped, renamed);
    }

    private async Task RestoreExternalIdentitiesAsync(
        JsonNode item,
        Guid targetUserId,
        bool replaceExisting,
        CancellationToken ct)
    {
        // Older backups do not contain canonical identities. Preserve whatever already
        // exists on overwrite and let the login mapper perform its guarded legacy upgrade.
        if (item["externalIdentities"] is not JsonArray identityNodes)
            return;

        var restored = new List<ExternalIdentity>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in identityNodes)
        {
            var authority = node?["authority"]?.GetValue<string>();
            var subject = node?["subject"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(subject))
                throw new InvalidDataException("Backup contains an external identity without authority or subject.");
            if (!keys.Add(authority + "\0" + subject))
                throw new InvalidDataException($"Backup contains duplicate external identity '{authority}/{subject}'.");

            var localConflict = db.ExternalIdentities.Local.FirstOrDefault(i =>
                i.Authority == authority && i.Subject == subject && i.UserId != targetUserId);
            var storedConflict = await db.ExternalIdentities.AsNoTracking().FirstOrDefaultAsync(i =>
                i.Authority == authority && i.Subject == subject && i.UserId != targetUserId, ct);
            if (localConflict is not null || storedConflict is not null)
            {
                throw new InvalidDataException(
                    $"External identity '{authority}/{subject}' already belongs to another user; restore will not merge users.");
            }

            restored.Add(new ExternalIdentity
            {
                Id = Guid.NewGuid(),
                UserId = targetUserId,
                Authority = authority,
                Subject = subject,
                CreatedAt = DateTime.TryParse(
                    node?["createdAt"]?.GetValue<string>(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var createdAt)
                    ? createdAt : DateTime.UtcNow,
                LastSeenAt = DateTime.TryParse(
                    node?["lastSeenAt"]?.GetValue<string>(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var lastSeenAt)
                    ? lastSeenAt : DateTime.UtcNow,
            });
        }

        if (replaceExisting)
        {
            var existing = await db.ExternalIdentities.Where(i => i.UserId == targetUserId).ToListAsync(ct);
            db.ExternalIdentities.RemoveRange(existing);
        }
        db.ExternalIdentities.AddRange(restored);
    }

    private async Task RestoreDirectoryMembershipsAsync(
        JsonNode item,
        Guid targetUserId,
        bool replaceExisting,
        CancellationToken ct)
    {
        if (item["directoryMemberships"] is not JsonArray nodes)
            return;

        var restored = new List<DirectoryMembership>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var authority = node?["authority"]?.GetValue<string>();
            var groupKey = node?["groupKey"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(groupKey))
                throw new InvalidDataException("Backup contains an incomplete directory membership.");
            if (!keys.Add(authority + "\0" + groupKey))
                throw new InvalidDataException("Backup contains a duplicate directory membership.");
            restored.Add(new DirectoryMembership
            {
                UserId = targetUserId,
                Authority = authority,
                GroupKey = groupKey,
                LastSeenAt = DateTime.TryParse(
                    node?["lastSeenAt"]?.GetValue<string>(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var lastSeenAt)
                    ? lastSeenAt
                    : DateTime.MinValue,
            });
        }

        if (replaceExisting)
        {
            var existing = await db.DirectoryMemberships
                .Where(membership => membership.UserId == targetUserId)
                .ToListAsync(ct);
            db.DirectoryMemberships.RemoveRange(existing);
        }
        db.DirectoryMemberships.AddRange(restored);
    }

    private async Task<SectionRestoreResult> RestoreFoldersAsync(RestoreState s, CancellationToken ct)
    {
        var policy = s.Policy(BackupSections.Folders);
        int created = 0, overwritten = 0, skipped = 0, renamed = 0;
        var structure = (s.Reader.Sections[BackupSections.Folders] as JsonObject)?["structure"] as JsonArray ?? [];

        // Id -> restored Path, so a child derives its Path from the *restored* parent Path instead of
        // the stale backup path. The export orders folders by Depth (parents first), so every parent
        // is already in this map when its children are processed. Seeded with the target DB's
        // pre-existing folders — an existing folder reused as a parent (Skip policy) must expose its
        // current Path to its restored children. Root is represented by a null ParentFolderId, so a
        // null parentTarget maps to the "" prefix. Without this, a parent renamed on conflict left its
        // children with the old backup Path while their ParentFolderId pointed at the renamed parent
        // → inconsistent materialized Path for the whole subtree.
        var pathById = new Dictionary<Guid, string>();
        foreach (var f in s.Folders.Values)
            pathById[f.Id] = f.Path == "/" ? "" : f.Path;

        s.FolderMap[SharedWorkflowFolder.RootFolderId] = SharedWorkflowFolder.RootFolderId;
        foreach (var item in structure)
        {
            var sourceId = Gid(item!["sourceId"]);
            if (sourceId == SharedWorkflowFolder.RootFolderId) { skipped++; continue; } // Root is fixed; never recreated.

            var name = item["name"]!.GetValue<string>();
            var depth = item["depth"]?.GetValue<int>() ?? 1;
            var parentSource = GidN(item["parentFolderId"]);
            var parentTarget = parentSource is null ? (Guid?)null : s.ResolveFolder(parentSource.Value);
            var createdBy = ResolveUserOrNull(s, GidN(item["createdByUserId"])); // remaps the folder-creator's user id; null if it can't be resolved (K17)

            // Recompute the Path from the parent's restored Path + this folder's name. The backup path
            // is only a serialization hint; the stored Path must follow the actual parent chain (which
            // may have been renamed above). Conflict detection runs on this recomputed path so a folder
            // clashes with whatever already lives at its true target position.
            var parentPath = parentTarget is null ? "" : pathById.GetValueOrDefault(parentTarget.Value, "");
            var path = parentPath.Length == 0 ? "/" + name : parentPath + "/" + name;

            if (s.Folders.TryGetValue(path, out var existing))
            {
                if (policy == RestoreConflictPolicy.Skip) { s.FolderMap[sourceId] = existing.Id; skipped++; continue; }
                if (policy == RestoreConflictPolicy.Overwrite)
                {
                    existing.Name = name; existing.ParentFolderId = parentTarget; existing.Depth = depth; existing.Path = path; existing.CreatedByUserId = createdBy;
                    pathById[existing.Id] = path;
                    s.FolderMap[sourceId] = existing.Id; overwritten++; continue;
                }
                // Rename: the DB enforces unique(ParentFolderId, Name), so we MUST give the new
                // folder a sibling-unique name and recompute the Path from it so the in-memory lookup
                // key tracks the actual stored Path.
                var siblingNames = new HashSet<string>(
                    s.Folders.Values.Where(f => f.ParentFolderId == parentTarget).Select(f => f.Name), StringComparer.Ordinal);
                name = UniqueName(name, siblingNames);
                path = parentPath.Length == 0 ? "/" + name : parentPath + "/" + name;
                renamed++;
            }
            else created++;

            var id = s.ExistingFolderIds.Contains(sourceId) ? Guid.NewGuid() : sourceId;
            var folder = new SharedWorkflowFolder
            {
                Id = id, Name = name, Path = path, Depth = depth, ParentFolderId = parentTarget, CreatedByUserId = createdBy,
            };
            db.SharedWorkflowFolders.Add(folder);
            s.Folders[path] = folder; s.ExistingFolderIds.Add(id); s.FolderMap[sourceId] = id;
            pathById[id] = path;
        }
        await db.SaveChangesAsync(ct);
        return new SectionRestoreResult(BackupSections.Folders, created, overwritten, skipped, renamed);
    }

    private async Task<SectionRestoreResult> RestoreCredentialsAsync(RestoreState s, CancellationToken ct)
    {
        var policy = s.Policy(BackupSections.Credentials);
        int created = 0, overwritten = 0, skipped = 0, renamed = 0;
        var takenNames = new HashSet<string>(s.Credentials.Keys, StringComparer.Ordinal);

        foreach (var item in Items(s.Reader, BackupSections.Credentials))
        {
            var sourceId = Gid(item!["sourceId"]);
            var name = item["name"]!.GetValue<string>();
            var username = item["username"]?.GetValue<string>() ?? "";
            var domain = item["domain"]?.GetValue<string>();
            var expiresAt = DateTime.TryParse(
                item["expiresAt"]?.GetValue<string>(), null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var exp) ? exp : (DateTime?)null;
            byte[] encrypted = EncryptedPasswordFor(item, s, name);

            if (s.Credentials.TryGetValue(name, out var existing))
            {
                if (policy == RestoreConflictPolicy.Skip) { s.CredentialMap[sourceId] = existing.Id; skipped++; continue; }
                if (policy == RestoreConflictPolicy.Overwrite)
                {
                    existing.Username = username; existing.Domain = domain; existing.EncryptedPassword = encrypted;
                    existing.ExpiresAt = expiresAt;
                    s.CredentialMap[sourceId] = existing.Id; overwritten++; continue;
                }
                name = UniqueName(name, takenNames); renamed++;
            }
            else created++;

            takenNames.Add(name);
            var id = s.ExistingCredentialIds.Contains(sourceId) ? Guid.NewGuid() : sourceId;
            var cred = new Credential { Id = id, Name = name, Username = username, Domain = domain, EncryptedPassword = encrypted, ExpiresAt = expiresAt };
            db.Credentials.Add(cred);
            s.Credentials[name] = cred; s.ExistingCredentialIds.Add(id); s.CredentialMap[sourceId] = id;
        }
        await db.SaveChangesAsync(ct);
        return new SectionRestoreResult(BackupSections.Credentials, created, overwritten, skipped, renamed);
    }

    private async Task<SectionRestoreResult> RestoreMachinesAsync(RestoreState s, CancellationToken ct)
    {
        var policy = s.Policy(BackupSections.Machines);
        int created = 0, overwritten = 0, skipped = 0, renamed = 0;
        var takenNames = new HashSet<string>(s.Machines.Keys, StringComparer.Ordinal);

        foreach (var item in Items(s.Reader, BackupSections.Machines))
        {
            var sourceId = Gid(item!["sourceId"]);
            var name = item["name"]!.GetValue<string>();
            var hostname = item["hostname"]?.GetValue<string>() ?? "";
            var winRmPort = item["winRmPort"]?.GetValue<int>() ?? 5985;
            var useSsl = item["useSsl"]?.GetValue<bool>() ?? false;
            var tags = item["tags"]?.GetValue<string>();
            var credSource = GidN(item["defaultCredentialId"]);
            var credTarget = credSource is null ? (Guid?)null : s.ResolveCredential(credSource.Value);

            if (s.Machines.TryGetValue(name, out var existing))
            {
                if (policy == RestoreConflictPolicy.Skip) { s.MachineMap[sourceId] = existing.Id; skipped++; continue; }
                if (policy == RestoreConflictPolicy.Overwrite)
                {
                    existing.Hostname = hostname; existing.WinRmPort = winRmPort; existing.UseSsl = useSsl;
                    existing.Tags = tags; existing.DefaultCredentialId = credTarget;
                    s.MachineMap[sourceId] = existing.Id; overwritten++; continue;
                }
                name = UniqueName(name, takenNames); renamed++;
            }
            else created++;

            takenNames.Add(name);
            var id = s.ExistingMachineIds.Contains(sourceId) ? Guid.NewGuid() : sourceId;
            var machine = new ManagedMachine
            {
                Id = id, Name = name, Hostname = hostname, WinRmPort = winRmPort, UseSsl = useSsl,
                Tags = tags, DefaultCredentialId = credTarget,
            };
            db.ManagedMachines.Add(machine);
            s.Machines[name] = machine; s.ExistingMachineIds.Add(id); s.MachineMap[sourceId] = id;
        }
        await db.SaveChangesAsync(ct);
        return new SectionRestoreResult(BackupSections.Machines, created, overwritten, skipped, renamed);
    }

    private async Task<SectionRestoreResult> RestoreGlobalFoldersAsync(RestoreState s, CancellationToken ct)
    {
        var policy = s.Policy(BackupSections.GlobalVariableFolders);
        int created = 0, overwritten = 0, skipped = 0, renamed = 0;
        var structure = (s.Reader.Sections[BackupSections.GlobalVariableFolders] as JsonObject)?["structure"] as JsonArray ?? [];

        // Id -> restored Path, so a child derives its Path from the *restored* parent Path instead of
        // the stale backup path. The export orders folders by Depth (parents first), so every parent
        // is already in this map when its children are processed. Seeded with Root (path prefix "") and
        // the target DB's pre-existing folders — an existing folder reused as a parent (Skip policy)
        // must expose its current Path to its restored children. Without this, a parent renamed on
        // conflict left its children with the old backup Path while their ParentFolderId pointed at the
        // renamed parent → inconsistent materialized Path for the whole subtree.
        var pathById = new Dictionary<Guid, string> { [GlobalVariableFolder.RootFolderId] = "" };
        foreach (var f in s.GlobalFolders.Values)
            pathById[f.Id] = f.Path == "/" ? "" : f.Path;

        s.GlobalFolderMap[GlobalVariableFolder.RootFolderId] = GlobalVariableFolder.RootFolderId;
        foreach (var item in structure)
        {
            var sourceId = Gid(item!["sourceId"]);
            if (sourceId == GlobalVariableFolder.RootFolderId) { skipped++; continue; } // Root is fixed; never recreated.

            var name = item["name"]!.GetValue<string>();
            var depth = item["depth"]?.GetValue<int>() ?? 1;
            var parentSource = GidN(item["parentFolderId"]);
            var parentTarget = parentSource is null ? GlobalVariableFolder.RootFolderId : (s.ResolveGlobalFolder(parentSource.Value) ?? GlobalVariableFolder.RootFolderId);
            var createdBy = ResolveUserOrNull(s, GidN(item["createdByUserId"]));

            // Recompute the Path from the parent's restored Path + this folder's name. The backup path
            // is only a serialization hint; the stored Path must follow the actual parent chain (which
            // may have been renamed above). Conflict detection runs on this recomputed path so a folder
            // clashes with whatever already lives at its true target position.
            var parentPath = pathById.GetValueOrDefault(parentTarget, "");
            var path = parentPath.Length == 0 ? "/" + name : parentPath + "/" + name;

            if (s.GlobalFolders.TryGetValue(path, out var existing))
            {
                if (policy == RestoreConflictPolicy.Skip) { s.GlobalFolderMap[sourceId] = existing.Id; skipped++; continue; }
                if (policy == RestoreConflictPolicy.Overwrite)
                {
                    existing.Name = name; existing.ParentFolderId = parentTarget; existing.Depth = depth; existing.Path = path; existing.CreatedByUserId = createdBy;
                    pathById[existing.Id] = path;
                    s.GlobalFolderMap[sourceId] = existing.Id; overwritten++; continue;
                }
                // Rename: unique(ParentFolderId, Name) forces a sibling-unique name; recompute the Path from it.
                var siblingNames = new HashSet<string>(
                    s.GlobalFolders.Values.Where(f => f.ParentFolderId == parentTarget).Select(f => f.Name), StringComparer.Ordinal);
                name = UniqueName(name, siblingNames);
                path = parentPath.Length == 0 ? "/" + name : parentPath + "/" + name;
                renamed++;
            }
            else created++;

            var id = s.ExistingGlobalFolderIds.Contains(sourceId) ? Guid.NewGuid() : sourceId;
            var folder = new GlobalVariableFolder
            {
                Id = id, Name = name, Path = path, Depth = depth, ParentFolderId = parentTarget, CreatedByUserId = createdBy,
            };
            db.GlobalVariableFolders.Add(folder);
            s.GlobalFolders[path] = folder; s.ExistingGlobalFolderIds.Add(id); s.GlobalFolderMap[sourceId] = id;
            pathById[id] = path;
        }
        await db.SaveChangesAsync(ct);
        return new SectionRestoreResult(BackupSections.GlobalVariableFolders, created, overwritten, skipped, renamed);
    }

    private async Task<SectionRestoreResult> RestoreGlobalsAsync(RestoreState s, CancellationToken ct)
    {
        var policy = s.Policy(BackupSections.GlobalVariables);
        int created = 0, overwritten = 0, skipped = 0, renamed = 0;
        var takenNames = new HashSet<string>(s.Globals.Keys, StringComparer.Ordinal);

        foreach (var item in Items(s.Reader, BackupSections.GlobalVariables))
        {
            var name = item!["name"]!.GetValue<string>();
            var isSecret = item["isSecret"]?.GetValue<bool>() ?? false;
            var description = item["description"]?.GetValue<string>();
            var storedValue = StoredGlobalValue(item, s, name, isSecret);
            // Remap the backed-up folderId onto its restored target; unknown/missing → Root.
            var folderSource = GidN(item["folderId"]);
            var folderId = folderSource is null ? GlobalVariableFolder.RootFolderId
                : (s.ResolveGlobalFolder(folderSource.Value) ?? GlobalVariableFolder.RootFolderId);

            if (s.Globals.TryGetValue(name, out var existing))
            {
                if (policy == RestoreConflictPolicy.Skip) { skipped++; continue; }
                if (policy == RestoreConflictPolicy.Overwrite)
                {
                    existing.IsSecret = isSecret; existing.Description = description;
                    existing.Value = storedValue; existing.FolderId = folderId; existing.UpdatedAt = DateTime.UtcNow;
                    overwritten++; continue;
                }
                name = UniqueName(name, takenNames); renamed++;
            }
            else created++;

            takenNames.Add(name);
            db.GlobalVariables.Add(new GlobalVariable
            {
                Id = Guid.NewGuid(), Name = name, Value = storedValue, IsSecret = isSecret,
                Description = description, FolderId = folderId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync(ct);
        return new SectionRestoreResult(BackupSections.GlobalVariables, created, overwritten, skipped, renamed);
    }

    private async Task<SectionRestoreResult> RestoreCustomActivitiesAsync(RestoreState s, CancellationToken ct)
    {
        var policy = s.Policy(BackupSections.CustomActivities);
        int created = 0, overwritten = 0, skipped = 0, renamed = 0;

        foreach (var item in Items(s.Reader, BackupSections.CustomActivities))
        {
            var sourceId = Gid(item!["sourceId"]);
            var key = item["key"]!.GetValue<string>();

            if (s.CustomActivities.TryGetValue(key, out var existing))
            {
                if (policy == RestoreConflictPolicy.Overwrite)
                {
                    ApplyCustomActivityFields(existing, item);
                    existing.UpdatedAt = DateTime.UtcNow;
                    s.CustomActivityMap[sourceId] = existing.Id;
                    overwritten++; continue;
                }
                // Skip OR Rename: a custom activity's Key is embedded in every referencing workflow
                // (custom:<key> activityType + __customKey), so it cannot be safely renamed on restore.
                // Both policies keep the existing definition and map references onto it.
                if (policy == RestoreConflictPolicy.Rename)
                    s.Warnings.Add($"Custom activity '{key}' already exists — keys cannot be renamed (they are embedded in workflow references); kept the existing definition.");
                s.CustomActivityMap[sourceId] = existing.Id;
                skipped++; continue;
            }

            created++;
            var id = s.ExistingCustomActivityIds.Contains(sourceId) ? Guid.NewGuid() : sourceId;
            var def = new CustomActivityDefinition
            {
                Id = id, Key = key, ConcurrencyToken = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            ApplyCustomActivityFields(def, item);
            db.CustomActivityDefinitions.Add(def);
            s.CustomActivities[key] = def; s.ExistingCustomActivityIds.Add(id); s.CustomActivityMap[sourceId] = id;
        }
        await db.SaveChangesAsync(ct);
        return new SectionRestoreResult(BackupSections.CustomActivities, created, overwritten, skipped, renamed);
    }

    private static void ApplyCustomActivityFields(CustomActivityDefinition def, JsonNode item)
    {
        def.Name = item["name"]!.GetValue<string>();
        def.Description = item["description"]?.GetValue<string>();
        def.Icon = item["icon"]?.GetValue<string>() ?? "extension";
        def.Color = item["color"]?.GetValue<string>();
        def.ScriptTemplate = item["scriptTemplate"]?.GetValue<string>() ?? "";
        def.Engine = item["engine"]?.GetValue<string>() ?? "auto";
        def.RunsRemote = item["runsRemote"]?.GetValue<bool>() ?? false;
        def.Isolated = item["isolated"]?.GetValue<bool>() ?? false;
        def.MemoryLimitMb = item["memoryLimitMb"]?.GetValue<int>();
        def.MaxProcesses = item["maxProcesses"]?.GetValue<int>();
        def.DefaultTimeoutSeconds = item["defaultTimeoutSeconds"]?.GetValue<int>();
        def.SuccessExitCodes = item["successExitCodes"]?.GetValue<string>();
        def.InputParametersJson = item["inputParametersJson"]?.GetValue<string>() ?? "[]";
        def.OutputParametersJson = item["outputParametersJson"]?.GetValue<string>() ?? "[]";
        def.IsEnabled = item["isEnabled"]?.GetValue<bool>() ?? false;
        def.Version = item["version"]?.GetValue<int>() ?? 1;
    }

    private async Task<SectionRestoreResult> RestoreWorkflowsAsync(RestoreState s, CancellationToken ct)
    {
        var policy = s.Policy(BackupSections.Workflows);
        int created = 0, overwritten = 0, skipped = 0, renamed = 0;
        var takenNames = new HashSet<string>(s.Workflows.Keys, StringComparer.Ordinal);

        foreach (var item in Items(s.Reader, BackupSections.Workflows))
        {
            var sourceId = Gid(item!["sourceId"]);
            var name = item["name"]!.GetValue<string>();
            var description = item["description"]?.GetValue<string>();
            var isEnabled = item["isEnabled"]?.GetValue<bool>() ?? false;
            var version = item["version"]?.GetValue<int>() ?? 1;
            var folderTarget = s.ResolveFolder(GidN(item["folderId"]) ?? SharedWorkflowFolder.RootFolderId)
                ?? SharedWorkflowFolder.RootFolderId;
            var definitionJson = RestoreDefinitionJson(item["definition"], s);

            if (s.Workflows.TryGetValue(name, out var existing))
            {
                if (policy == RestoreConflictPolicy.Skip) { s.WorkflowMap[sourceId] = existing.Id; skipped++; continue; }
                if (policy == RestoreConflictPolicy.Overwrite)
                {
                    if (existing.CheckedOutByUserId is not null)
                        throw new BackupRestoreException(
                            $"Restore aborted: workflow '{existing.Name}' is locked for editing. Publish, unlock, or force-unlock it before overwrite restore.");
                    existing.Description = description; existing.DefinitionJson = definitionJson;
                    existing.IsEnabled = isEnabled; existing.FolderId = folderTarget; existing.UpdatedAt = DateTime.UtcNow;
                    s.WorkflowMap[sourceId] = existing.Id; overwritten++; continue;
                }
                name = UniqueName(name, takenNames); renamed++;
            }
            else created++;

            takenNames.Add(name);
            var id = s.ExistingWorkflowIds.Contains(sourceId) ? Guid.NewGuid() : sourceId;
            var wf = new Workflow
            {
                Id = id, Name = name, Description = description, DefinitionJson = definitionJson,
                Version = version, IsEnabled = isEnabled, FolderId = folderTarget,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Workflows.Add(wf);
            s.Workflows[name] = wf; s.ExistingWorkflowIds.Add(id); s.WorkflowMap[sourceId] = id;
        }
        await db.SaveChangesAsync(ct);
        return new SectionRestoreResult(BackupSections.Workflows, created, overwritten, skipped, renamed);
    }

    private async Task<SectionRestoreResult> RestoreAlertingAsync(RestoreState s, CancellationToken ct)
    {
        var policy = s.Policy(BackupSections.Alerting);
        int created = 0, overwritten = 0, skipped = 0, renamed = 0;
        var takenNames = new HashSet<string>(s.NotificationRules.Keys, StringComparer.Ordinal);

        foreach (var item in Items(s.Reader, BackupSections.Alerting))
        {
            var name = item!["name"]!.GetValue<string>();
            var kind = Enum.TryParse<NotificationRuleKind>(item["kind"]?.GetValue<string>(), out var k) ? k : NotificationRuleKind.Custom;
            var isEnabled = item["isEnabled"]?.GetValue<bool>() ?? false;

            if (s.NotificationRules.TryGetValue(name, out var existing))
            {
                if (policy == RestoreConflictPolicy.Skip) { skipped++; continue; }
                if (policy == RestoreConflictPolicy.Overwrite)
                {
                    ApplyRuleScalars(existing, item, kind, isEnabled);
                    db.NotificationRoutes.RemoveRange(existing.Routes);
                    db.NotificationRuleTargets.RemoveRange(existing.Targets);
                    foreach (var r in RestoredRoutes(item, s, existing.Id)) db.NotificationRoutes.Add(r);
                    foreach (var tg in RestoredTargets(item, s, existing.Id)) db.NotificationRuleTargets.Add(tg);
                    overwritten++; continue;
                }
                name = UniqueName(name, takenNames); renamed++;
            }
            else created++;

            takenNames.Add(name);
            var id = Guid.NewGuid();
            var rule = new NotificationRule { Id = id, Name = name };
            ApplyRuleScalars(rule, item, kind, isEnabled);
            rule.Routes = RestoredRoutes(item, s, id);
            rule.Targets = RestoredTargets(item, s, id);
            db.NotificationRules.Add(rule);
            s.NotificationRules[name] = rule; s.ExistingNotificationRuleIds.Add(id);
        }
        await db.SaveChangesAsync(ct);
        return new SectionRestoreResult(BackupSections.Alerting, created, overwritten, skipped, renamed);
    }

    private static void ApplyRuleScalars(NotificationRule rule, JsonNode item, NotificationRuleKind kind, bool isEnabled)
    {
        rule.Description = item["description"]?.GetValue<string>();
        rule.IsEnabled = isEnabled;
        rule.Kind = kind;
        rule.EventTypes = item["eventTypes"]?.GetValue<string>() ?? "";
        rule.FilterExpressionJson = item["filterExpressionJson"]?.GetValue<string>();
        rule.ScopeKind = Enum.TryParse<NotificationScopeKind>(item["scopeKind"]?.GetValue<string>(), out var sc) ? sc : NotificationScopeKind.Global;
        rule.CooldownMinutes = item["cooldownMinutes"]?.GetValue<int>() ?? 0;
        rule.DedupKeyTemplate = item["dedupKeyTemplate"]?.GetValue<string>();
        rule.MinOccurrences = item["minOccurrences"]?.GetValue<int>() ?? 1;
        rule.OccurrenceWindowMinutes = item["occurrenceWindowMinutes"]?.GetValue<int>() ?? 0;
        rule.SystemSourceId = item["systemSourceId"]?.GetValue<string>();
        rule.SystemPresetId = item["systemPresetId"]?.GetValue<string>();
        rule.SourceParametersJson = item["sourceParametersJson"]?.GetValue<string>();
        rule.SustainForSeconds = item["sustainForSeconds"]?.GetValue<int>() ?? 0;
        rule.SeverityOverride = Enum.TryParse<NotificationSeverity>(item["severityOverride"]?.GetValue<string>(), out var sev) ? sev : null;
        // A restored enabled System policy gets a fresh activation watermark so it never back-alerts history.
        rule.ActivatedAt = kind == NotificationRuleKind.System && isEnabled ? DateTime.UtcNow : null;
    }

    private List<NotificationRoute> RestoredRoutes(JsonNode item, RestoreState s, Guid ruleId)
    {
        var routes = new List<NotificationRoute>();
        var order = 0;
        foreach (var rn in (item["routes"] as JsonArray) ?? [])
        {
            var channel = Enum.TryParse<NotificationChannel>(rn!["channel"]?.GetValue<string>(), out var ch) ? ch : NotificationChannel.Email;
            var plaintext = DecryptField(rn["secret"], s.Protector);
            routes.Add(new NotificationRoute
            {
                Id = Guid.NewGuid(),
                NotificationRuleId = ruleId,
                Channel = channel,
                Target = rn["target"]?.GetValue<string>() ?? "",
                Secret = plaintext is null ? null : Convert.ToBase64String(atRest.Protect(plaintext)),
                ConditionExpressionJson = rn["conditionExpressionJson"]?.GetValue<string>(),
                Order = order++,
            });
        }
        return routes;
    }

    // Remaps scope targets onto restored folder/workflow ids; a target that resolves to nothing (its
    // folder/workflow was not in the backup and doesn't exist here) is dropped with a warning — targets are
    // soft references, so a missing one must not abort the restore.
    private static List<NotificationRuleTarget> RestoredTargets(JsonNode item, RestoreState s, Guid ruleId)
    {
        var targets = new List<NotificationRuleTarget>();
        foreach (var tn in (item["targets"] as JsonArray) ?? [])
        {
            if (!Enum.TryParse<NotificationTargetKind>(tn!["targetKind"]?.GetValue<string>(), out var kind)) continue;
            var sourceId = Gid(tn["targetId"]);
            Guid? mapped = kind == NotificationTargetKind.Folder
                ? (s.FolderMap.TryGetValue(sourceId, out var f) ? f : s.ExistingFolderIds.Contains(sourceId) ? sourceId : null)
                : (s.WorkflowMap.TryGetValue(sourceId, out var w) ? w : s.ExistingWorkflowIds.Contains(sourceId) ? sourceId : null);
            if (mapped is null)
            {
                s.Warnings.Add($"Alerting rule '{item["name"]}' dropped a {kind} scope target that no longer resolves.");
                continue;
            }
            targets.Add(new NotificationRuleTarget { Id = Guid.NewGuid(), NotificationRuleId = ruleId, TargetKind = kind, TargetId = mapped.Value });
        }
        return targets;
    }

    private async Task<SectionRestoreResult> RestoreGrantsAsync(RestoreState s, CancellationToken ct)
    {
        var policy = s.Policy(BackupSections.Folders);
        int created = 0, overwritten = 0, skipped = 0;
        var grants = (s.Reader.Sections[BackupSections.Folders] as JsonObject)?["grants"] as JsonArray ?? [];
        var existing = await db.SharedFolderPermissions.ToListAsync(ct);

        foreach (var g in grants)
        {
            var folderTarget = s.ResolveFolder(Gid(g!["folderId"]));
            if (folderTarget is null) { s.Warnings.Add("Skipped a folder grant whose folder could not be resolved."); continue; }

            var principalType = Enum.Parse<FolderPrincipalType>(g["principalType"]!.GetValue<string>());
            var role = Enum.Parse<SharedFolderRole>(g["role"]!.GetValue<string>());
            var principalKey = g["principalKey"]!.GetValue<string>();
            var principalAuthority = principalType == FolderPrincipalType.Group
                ? g["principalAuthority"]?.GetValue<string>()
                    ?? ExternalIdentity.ActiveDirectoryAuthority
                : string.Empty;
            if (principalKey.Length > 256)
                throw new BackupRestoreException("Folder grant PrincipalKey exceeds 256 characters.");
            if (principalType == FolderPrincipalType.Group)
            {
                if (principalAuthority.Length > 512)
                    throw new BackupRestoreException("Folder grant PrincipalAuthority exceeds 512 characters.");
                if (principalAuthority == ExternalIdentity.ActiveDirectoryAuthority)
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(
                            principalKey, @"^S-\d+-\d+(-\d+)+$",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                            TimeSpan.FromSeconds(1)))
                        throw new BackupRestoreException("Active Directory folder grant contains an invalid group SID.");
                    principalKey = principalKey.ToUpperInvariant();
                }
                else if (!NodePilot.Api.Security.Oidc.OidcIdentityMapper.IsValidIssuer(principalAuthority))
                {
                    throw new BackupRestoreException(
                        "OIDC/SCIM folder grant contains an invalid HTTPS issuer authority.");
                }
            }
            if (principalType == FolderPrincipalType.User && Guid.TryParse(principalKey, out var pid))
            {
                var mapped = ResolveUserOrNull(s, pid);
                if (mapped is null) { s.Warnings.Add($"Skipped a folder grant for an unresolvable user ({principalKey})."); continue; }
                principalKey = mapped.Value.ToString();
            }
            var grantedBy = ResolveUserOrNull(s, GidN(g["grantedByUserId"]));

            var match = existing.FirstOrDefault(p => p.FolderId == folderTarget && p.PrincipalType == principalType
                && string.Equals(
                    string.IsNullOrWhiteSpace(p.PrincipalAuthority) && principalType == FolderPrincipalType.Group
                        ? ExternalIdentity.ActiveDirectoryAuthority
                        : p.PrincipalAuthority,
                    principalAuthority,
                    StringComparison.Ordinal)
                && string.Equals(p.PrincipalKey, principalKey, StringComparison.Ordinal));
            if (match is not null)
            {
                if (policy == RestoreConflictPolicy.Overwrite)
                {
                    match.Role = role;
                    match.PrincipalAuthority = principalAuthority;
                    overwritten++;
                }
                else skipped++;
                continue;
            }

            db.SharedFolderPermissions.Add(new SharedFolderPermission
            {
                Id = Guid.NewGuid(), FolderId = folderTarget.Value, PrincipalType = principalType,
                PrincipalAuthority = principalAuthority, PrincipalKey = principalKey,
                Role = role, GrantedByUserId = grantedBy, GrantedAt = DateTime.UtcNow,
            });
            created++;
        }
        await db.SaveChangesAsync(ct);
        return new SectionRestoreResult("folderGrants", created, overwritten, skipped, 0);
    }

    private SettingsRestoreResult RestoreSettings(BackupFileReader reader, PassphraseSecretProtector protector, List<string> warnings)
    {
        try
        {
            var runtimeJson = (reader.Sections[BackupSections.Settings] as JsonObject)?["runtimeJson"] as JsonObject;
            if (runtimeJson is null) return new SettingsRestoreResult(false, "No runtime settings in backup.");

            overrides.MutateAndWrite(root =>
            {
                // Replace, don't merge: a restore must reproduce the backup's runtime-override
                // state, so any top-level override that exists in the target but NOT in the backup
                // is removed. __meta (transient restart-marker bookkeeping) is preserved.
                var keep = new HashSet<string>(
                    runtimeJson.Select(kv => kv.Key).Append(RuntimeOverridesWriter.MetaSectionKey), StringComparer.Ordinal);
                foreach (var staleKey in root.Select(kv => kv.Key).Where(k => !keep.Contains(k)).ToList())
                    root.Remove(staleKey);

                foreach (var (key, value) in runtimeJson)
                {
                    if (key == RuntimeOverridesWriter.MetaSectionKey) continue;
                    root[key] = value is null ? null : RewrapSettingValue(value, protector);
                }
            });
            return new SettingsRestoreResult(true, "Runtime settings replaced. A service restart may be required to take effect.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Settings restore failed (DB sections already committed).");
            return new SettingsRestoreResult(false, $"Settings restore failed: {ex.Message}");
        }
    }

    // ---- validation: confirm every referenced id resolves before any write happens (K12) ----

    private async Task ValidateReferencesAsync(RestoreState s, CancellationToken ct)
    {
        await Task.CompletedTask;
        var unresolved = new List<string>();

        // machines → credentials
        foreach (var m in Items(s.Reader, BackupSections.Machines))
        {
            var c = GidN(m!["defaultCredentialId"]);
            if (c is not null && !s.CredentialResolvable(c.Value)) unresolved.Add($"machine '{m["name"]}' → credential {c}");
        }
        // workflows → folder + definition refs
        foreach (var w in Items(s.Reader, BackupSections.Workflows))
        {
            var f = GidN(w!["folderId"]);
            if (f is not null && f != SharedWorkflowFolder.RootFolderId && !s.FolderResolvable(f.Value))
                unresolved.Add($"workflow '{w["name"]}' → folder {f}");
            if (w["definition"] is JsonNode def)
                foreach (var (kind, id) in ExtractDefinitionRefs(def))
                {
                    var ok = kind == "targetMachineId" ? s.MachineResolvable(id) : s.CredentialResolvable(id);
                    if (!ok) unresolved.Add($"workflow '{w["name"]}' → {kind} {id}");
                }
        }
        // folder structure → parent
        var structure = (s.Reader.Sections[BackupSections.Folders] as JsonObject)?["structure"] as JsonArray ?? [];
        foreach (var fo in structure)
        {
            var p = GidN(fo!["parentFolderId"]);
            if (p is not null && p != SharedWorkflowFolder.RootFolderId && !s.FolderResolvable(p.Value))
                unresolved.Add($"folder '{fo["name"]}' → parent {p}");
        }
        // global-variable folder structure → parent
        var gStructure = (s.Reader.Sections[BackupSections.GlobalVariableFolders] as JsonObject)?["structure"] as JsonArray ?? [];
        foreach (var fo in gStructure)
        {
            var p = GidN(fo!["parentFolderId"]);
            if (p is not null && p != GlobalVariableFolder.RootFolderId && !s.GlobalFolderResolvable(p.Value))
                unresolved.Add($"global-variable folder '{fo["name"]}' → parent {p}");
        }
        // globals → folder
        foreach (var v in Items(s.Reader, BackupSections.GlobalVariables))
        {
            var f = GidN(v!["folderId"]);
            if (f is not null && f != GlobalVariableFolder.RootFolderId && !s.GlobalFolderResolvable(f.Value))
                unresolved.Add($"global variable '{v["name"]}' → folder {f}");
        }

        if (unresolved.Count > 0)
            throw new BackupRestoreException(
                "Restore aborted — unresolvable references (the referenced section is neither in the backup nor already present): "
                + string.Join("; ", unresolved.Take(20)) + (unresolved.Count > 20 ? $" (+{unresolved.Count - 20} more)" : ""));
    }

    // ---- helpers ----

    private static HashSet<(string Authority, string Subject)> IncomingIdentityKeys(
        JsonNode item,
        AuthProvider provider,
        string? externalId)
    {
        var keys = new HashSet<(string Authority, string Subject)>();
        if (item["externalIdentities"] is JsonArray identities)
        {
            foreach (var identity in identities)
            {
                var authority = identity?["authority"]?.GetValue<string>();
                var subject = identity?["subject"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(authority) && !string.IsNullOrWhiteSpace(subject))
                    keys.Add((authority, subject));
            }
            return keys;
        }

        // Legacy backups predate ExternalIdentity. Only providers whose compatibility
        // ExternalId had a defined authority can be matched safely; OIDC issuer context
        // is absent and therefore requires an exact source-id or Rename.
        if (!string.IsNullOrWhiteSpace(externalId))
        {
            if (provider == AuthProvider.Ldap)
                keys.Add((ExternalIdentity.LegacyLdapAuthority, externalId));
            else if (provider == AuthProvider.Windows)
                keys.Add((ExternalIdentity.ActiveDirectoryAuthority, externalId));
        }
        return keys;
    }

    private static bool IsSameRestoredIdentity(
        User existing,
        Guid sourceId,
        AuthProvider provider,
        string? externalId,
        IReadOnlySet<(string Authority, string Subject)> incomingIdentityKeys)
    {
        if (provider == AuthProvider.Local)
            return existing.Id == sourceId && existing.Provider == AuthProvider.Local;
        // A modern backup carries the canonical authority-qualified identities. Once
        // those are present, neither a matching source GUID nor the legacy provider /
        // ExternalId alias may weaken that boundary (OIDC sub is not issuer-global).
        if (incomingIdentityKeys.Count > 0)
            return existing.ExternalIdentities.Any(identity =>
                incomingIdentityKeys.Contains((identity.Authority, identity.Subject)));

        // Compatibility is intentionally limited to backups that predate the canonical
        // ExternalIdentity array. Their exact source id is the only remaining anchor.
        return existing.Id == sourceId
               && existing.Provider == provider
               && string.Equals(existing.ExternalId, externalId, StringComparison.Ordinal);
    }

    private async Task LoadExistingAsync(RestoreState s, CancellationToken ct)
    {
        foreach (var u in await db.Users.Include(user => user.ExternalIdentities).ToListAsync(ct))
        {
            s.Users[u.Username] = u;
            s.UsersById[u.Id] = u;
            s.ExistingUserIds.Add(u.Id);
        }
        foreach (var f in await db.SharedWorkflowFolders.ToListAsync(ct)) { s.Folders[f.Path] = f; s.ExistingFolderIds.Add(f.Id); }
        foreach (var c in await db.Credentials.ToListAsync(ct)) { s.Credentials[c.Name] = c; s.ExistingCredentialIds.Add(c.Id); }
        foreach (var m in await db.ManagedMachines.ToListAsync(ct)) { s.Machines[m.Name] = m; s.ExistingMachineIds.Add(m.Id); }
        foreach (var f in await db.GlobalVariableFolders.ToListAsync(ct)) { s.GlobalFolders[f.Path] = f; s.ExistingGlobalFolderIds.Add(f.Id); }
        foreach (var v in await db.GlobalVariables.ToListAsync(ct)) s.Globals[v.Name] = v;
        foreach (var d in await db.CustomActivityDefinitions.Where(d => !d.IsDeleted).ToListAsync(ct))
        { s.CustomActivities[d.Key] = d; s.ExistingCustomActivityIds.Add(d.Id); }
        foreach (var w in await db.Workflows.ToListAsync(ct)) { s.Workflows[w.Name] = w; s.ExistingWorkflowIds.Add(w.Id); }
        foreach (var r in await db.NotificationRules.ToListAsync(ct)) { s.NotificationRules[r.Name] = r; s.ExistingNotificationRuleIds.Add(r.Id); }
    }

    private string RestoreDefinitionJson(JsonNode? definition, RestoreState s)
    {
        if (definition is null) return "{\"nodes\":[],\"edges\":[]}";
        var unresolved = new List<string>();
        var node = WorkflowDefinitionSecretRewriter.RestoreDefinition(
            definition, s.Protector,
            g => s.ResolveMachine(g), g => s.ResolveCredential(g), unresolved);
        // Validation already guaranteed resolvability; this is belt-and-suspenders.
        if (unresolved.Count > 0)
            throw new BackupRestoreException("Workflow definition has unresolvable references: " + string.Join(", ", unresolved));
        // Remap custom-activity node references (config.__customDefinitionId) onto their restored ids.
        // Custom activities restore before workflows, so the map is complete here. Handles the
        // overwrite-merge case where the live id differs from the backed-up source id; a no-op for a
        // clean DR restore (source ids preserved).
        RemapCustomActivityRefs(node, s);
        return node.ToJsonString();
    }

    private byte[] EncryptedPasswordFor(JsonNode item, RestoreState s, string name)
    {
        var plaintext = DecryptField(item["password"], s.Protector);
        if (plaintext is null)
        {
            s.Warnings.Add($"Credential '{name}' had no recoverable password in the backup — restored with an empty password; re-enter it.");
            plaintext = "";
        }
        return atRest.Protect(plaintext);
    }

    private string StoredGlobalValue(JsonNode item, RestoreState s, string name, bool isSecret)
    {
        if (!isSecret) return item["value"]?.GetValue<string>() ?? "";
        var plaintext = DecryptField(item["value"], s.Protector);
        if (plaintext is null)
        {
            s.Warnings.Add($"Global '{name}' had no recoverable secret value in the backup — restored empty; re-enter it.");
            return Convert.ToBase64String(atRest.Protect(""));
        }
        return Convert.ToBase64String(atRest.Protect(plaintext)); // matches GlobalVariableStore.Encode
    }

    /// <summary>
    /// Reverses the backup's <c>$enc</c> wrapping for a runtime-settings value and re-seals it in the
    /// <c>enc:v1:</c> at-rest form (K9) so secrets never land in <c>appsettings.runtime.json</c> as
    /// plaintext. The EncryptingJsonConfigurationProvider transparently decrypts these on next load.
    /// </summary>
    private JsonNode RewrapSettingValue(JsonNode node, PassphraseSecretProtector protector)
    {
        switch (node)
        {
            case JsonObject enc when enc.Count == 1 && enc.TryGetPropertyValue(WorkflowDefinitionSecretRewriter.EncKey, out var b64)
                && b64 is JsonValue bv && bv.TryGetValue(out string? s) && s is not null:
            {
                var plaintext = protector.Unprotect(Convert.FromBase64String(s));
                var resealed = EncryptingJsonConfigurationProvider.EncryptedValuePrefix
                    + Convert.ToBase64String(atRest.Protect(plaintext));
                return JsonValue.Create(resealed);
            }
            case JsonObject obj:
            {
                var r = new JsonObject();
                foreach (var (k, v) in obj) r[k] = v is null ? null : RewrapSettingValue(v, protector);
                return r;
            }
            case JsonArray arr:
            {
                var r = new JsonArray();
                foreach (var v in arr) r.Add(v is null ? null : RewrapSettingValue(v, protector));
                return r;
            }
            default:
                return node.DeepClone();
        }
    }

    private static Guid? ResolveUserOrNull(RestoreState s, Guid? source)
    {
        if (source is null) return null;
        if (s.UserMap.TryGetValue(source.Value, out var t)) return t;
        return s.ExistingUserIds.Contains(source.Value) ? source.Value : null; // K17 — null when unresolvable
    }

    /// <summary>Recursively remaps <c>config.__customDefinitionId</c> GUIDs onto their restored target ids.</summary>
    private static void RemapCustomActivityRefs(JsonNode node, RestoreState s)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["__customDefinitionId"] is JsonValue v && v.TryGetValue(out string? idStr)
                    && Guid.TryParse(idStr, out var oldId)
                    && s.ResolveCustomActivity(oldId) is { } target && target != oldId)
                {
                    obj["__customDefinitionId"] = target.ToString();
                }
                foreach (var (_, child) in obj) if (child is not null) RemapCustomActivityRefs(child, s);
                break;
            case JsonArray arr:
                foreach (var item in arr) if (item is not null) RemapCustomActivityRefs(item, s);
                break;
        }
    }

    private static IEnumerable<(string kind, Guid id)> ExtractDefinitionRefs(JsonNode node, string? parentName = null)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, value) in obj)
                    if (value is not null)
                        foreach (var r in ExtractDefinitionRefs(value, name)) yield return r;
                break;
            case JsonArray arr:
                foreach (var item in arr)
                    if (item is not null)
                        foreach (var r in ExtractDefinitionRefs(item, parentName)) yield return r;
                break;
            case JsonValue val when (parentName == "targetMachineId" || parentName == "credentialId")
                && val.TryGetValue(out string? str) && Guid.TryParse(str, out var g):
                yield return (parentName!, g);
                break;
        }
    }

    private static string? DecryptField(JsonNode? field, PassphraseSecretProtector protector)
    {
        if (field is JsonObject obj && obj.TryGetPropertyValue(WorkflowDefinitionSecretRewriter.EncKey, out var b64)
            && b64 is JsonValue v && v.TryGetValue(out string? s) && s is not null)
            return protector.Unprotect(Convert.FromBase64String(s));
        return null;
    }

    private static JsonArray Items(BackupFileReader reader, string section) =>
        (reader.Sections[section] as JsonObject)?["items"] as JsonArray ?? [];

    private static Guid Gid(JsonNode? n) => Guid.Parse(n!.GetValue<string>());
    private static Guid? GidN(JsonNode? n)
    {
        var s = n?.GetValue<string>();
        return string.IsNullOrEmpty(s) ? null : Guid.Parse(s);
    }

    private static string UniqueName(string desired, HashSet<string> taken)
    {
        if (!taken.Contains(desired)) return desired;
        for (var n = 2; n < 1000; n++)
        {
            var candidate = $"{desired} (Restored {n})";
            if (!taken.Contains(candidate)) return candidate;
        }
        return $"{desired} (Restored {Guid.NewGuid():N})";
    }
}
