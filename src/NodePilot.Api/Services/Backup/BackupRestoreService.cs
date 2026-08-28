using System.Data;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NodePilot.Api.Configuration;
using NodePilot.Api.Security;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Core.Validation;
using NodePilot.Data;
using NodePilot.Data.Security;

namespace NodePilot.Api.Services.Backup;

/// <summary>
/// Restores an authenticated <c>nodepilot-system-backup/v4</c> archive (ADR 0001). Preview and
/// restore both require the passphrase and decrypt the complete configuration payload before it is inspected. The service validates
/// that every hard reference resolves (K12), then writes all DB sections in one transaction in
/// dependency order (K4) while building source to target id-maps (K3). Workflow-definition GUID
/// references are remapped (K13), user sessions are invalidated on overwrite (K16), the last active
/// admin is protected (K11), and settings-file changes are compensated if the DB commit fails (K8).
/// </summary>
public sealed class BackupRestoreService(
    NodePilotDbContext db,
    ISecretProtector atRest,
    RuntimeOverridesWriter overrides,
    ILogger<BackupRestoreService> logger,
    NodePilot.Api.Services.WorkflowVersionDefinitionProtector versionDefinitions)
{
    private const string RestoreCommitMarkerAction = "BACKUP_RESTORE_DB_COMMITTED";

    // ---- Preview ------------------------------------------------------------

    public async Task<BackupPreviewResult> PreviewAsync(byte[] content, string? passphrase, CancellationToken ct)
    {
        var reader = BackupFileReader.Parse(content);
        if (string.IsNullOrEmpty(passphrase))
            throw new BackupRestoreException("A passphrase is required to preview this fully encrypted backup.");
        _ = reader.TryUnlock(passphrase)
            ?? throw new BackupRestoreException("Passphrase is incorrect.");

        var sections = new List<BackupPreviewSection>();
        foreach (var key in RestoreOrder.Concat([BackupSections.Settings]))
        {
            if (reader.Sections[key] is null) continue;
            sections.Add(await PreviewSectionAsync(key, reader, ct));
        }

        return new BackupPreviewResult(true, reader.AppVersion, sections, []);
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

    // Restore dependencies in array order. Runtime settings participate through an atomic file
    // replacement plus compensation if the database transaction cannot commit.
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
        // a retry starts clean. SQLite (tests) returns a non-retrying strategy -> runs once.
        var results = new List<SectionRestoreResult>();
        var warnings = new List<string>();
        var restoresSettings = reader.Sections[BackupSections.Settings] is not null;
        var originalSettings = restoresSettings ? overrides.ReadOrEmpty() : null;
        var restoredSettings = restoresSettings ? BuildRestoredSettings(reader, protector) : null;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            results.Clear();
            var ctx = new RestoreState(reader, protector, policies);
            await LoadExistingAsync(ctx, ct);
            ValidateReferences(ctx); // K12 — abort before any write

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (restoresUsers)
                await AdminAccountMutationGate.AcquireTransactionLockAsync(db, ct);
            var settingsApplied = false;
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

                if (ctx.Warnings.Count > 0)
                    throw new BackupRestoreException(
                        "Restore aborted because it would be incomplete: " + string.Join(" | ", ctx.Warnings));

                await db.SaveChangesAsync(ct);
                if (restoredSettings is not null)
                {
                    overrides.ReplaceAll(restoredSettings);
                    settingsApplied = true;
                }
                await tx.CommitAsync(ct);
            }
            catch
            {
                if (settingsApplied && originalSettings is not null)
                {
                    try { overrides.ReplaceAll(originalSettings); }
                    catch (Exception compensationError)
                    {
                        logger.LogCritical(
                            compensationError,
                            "Backup restore could not compensate runtime settings after database rollback.");
                        throw new BackupRestoreException(
                            "Restore failed and the original runtime settings could not be restored. Manual recovery is required.");
                    }
                }
                await tx.RollbackAsync(ct);
                throw;
            }
            warnings.Clear();
            warnings.AddRange(ctx.Warnings);
        });

        SettingsRestoreResult? settings = restoresSettings
            ? new SettingsRestoreResult(
                true,
                "Runtime settings replaced atomically with the database restore. A service restart may be required.")
            : null;

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
                    // K16 — bump SecurityStamp (invalidate live sessions) on a security-relevant
                    // change.
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

    private Task<SectionRestoreResult> RestoreFoldersAsync(RestoreState s, CancellationToken ct) =>
        RestoreFolderStructureAsync(
            s,
            BackupSections.Folders,
            SharedWorkflowFolder.RootFolderId,
            s.Folders,
            s.ExistingFolderIds,
            s.FolderMap,
            // Root is represented by a null ParentFolderId here, and an unresolvable parent stays
            // null.
            parentSource => parentSource is null ? null : s.ResolveFolder(parentSource.Value),
            FolderTrees.Shared,
            folder => db.SharedWorkflowFolders.Add(folder),
            ct);

    private Task<SectionRestoreResult> RestoreGlobalFoldersAsync(RestoreState s, CancellationToken ct) =>
        RestoreFolderStructureAsync(
            s,
            BackupSections.GlobalVariableFolders,
            GlobalVariableFolder.RootFolderId,
            s.GlobalFolders,
            s.ExistingGlobalFolderIds,
            s.GlobalFolderMap,
            // Unlike shared folders, a missing/unresolvable parent lands under the singleton Root.
            parentSource => parentSource is null
                ? GlobalVariableFolder.RootFolderId
                : s.ResolveGlobalFolder(parentSource.Value) ?? GlobalVariableFolder.RootFolderId,
            FolderTrees.Global,
            folder => db.GlobalVariableFolders.Add(folder),
            ct);

    /// <summary>
    /// Restores one folder tree's <c>structure</c> array. Identical for both folder types
    /// (see <see cref="FolderTreeShape{TFolder}"/>); only the root id, the parent-resolution rule
    /// and the target DbSet differ.
    /// </summary>
    private async Task<SectionRestoreResult> RestoreFolderStructureAsync<TFolder>(
        RestoreState s,
        string section,
        Guid rootId,
        IDictionary<string, TFolder> byPath,
        HashSet<Guid> existingIds,
        IDictionary<Guid, Guid> folderMap,
        Func<Guid?, Guid?> resolveParent,
        FolderTreeShape<TFolder> shape,
        Action<TFolder> add,
        CancellationToken ct)
    {
        var policy = s.Policy(section);
        int created = 0, overwritten = 0, skipped = 0, renamed = 0;
        var structure = (s.Reader.Sections[section] as JsonObject)?["structure"] as JsonArray ?? [];

        // Id -> restored Path, so a child derives its Path from the *restored* parent Path instead
        // of the stale backup path. The export orders folders by Depth (parents first), so every
        // parent is already in this map when its children are processed. Seeded with Root (path
        // prefix "") and the target DB's pre-existing folders — an existing folder reused as a
        // parent (Skip policy) must expose its current Path to its restored children. Without this,
        // a parent renamed on conflict left its children with the old backup Path while their
        // ParentFolderId pointed at the renamed parent -> inconsistent materialized Path for the
        // whole subtree.
        var pathById = new Dictionary<Guid, string> { [rootId] = "" };
        foreach (var f in byPath.Values)
            pathById[shape.Id(f)] = shape.Path(f) == "/" ? "" : shape.Path(f);

        folderMap[rootId] = rootId;
        foreach (var item in structure)
        {
            var sourceId = Gid(item!["sourceId"]);
            if (sourceId == rootId) { skipped++; continue; } // Root is fixed; never recreated.

            var name = item["name"]!.GetValue<string>();
            var depth = item["depth"]?.GetValue<int>() ?? 1;
            var parentTarget = resolveParent(GidN(item["parentFolderId"]));
            var createdBy = ResolveUserOrNull(s, GidN(item["createdByUserId"])); // remaps the folder-creator's user id; null if it can't be resolved (K17)

            // Recompute the Path from the parent's restored Path + this folder's name. The backup
            // path is only a serialization hint; the stored Path must follow the actual parent
            // chain (which may have been renamed above). Conflict detection runs on this recomputed
            // path so a folder clashes with whatever already lives at its true target position.
            var parentPath = parentTarget is null ? "" : pathById.GetValueOrDefault(parentTarget.Value, "");
            var path = parentPath.Length == 0 ? "/" + name : parentPath + "/" + name;

            if (byPath.TryGetValue(path, out var existing))
            {
                if (policy == RestoreConflictPolicy.Skip) { folderMap[sourceId] = shape.Id(existing); skipped++; continue; }
                if (policy == RestoreConflictPolicy.Overwrite)
                {
                    shape.Apply(existing, name, path, depth, parentTarget, createdBy);
                    pathById[shape.Id(existing)] = path;
                    folderMap[sourceId] = shape.Id(existing); overwritten++; continue;
                }
                // Rename: the DB enforces unique(ParentFolderId, Name), so we must give the new
                // folder a sibling-unique name and recompute the Path from it so the in-memory
                // lookup key tracks the actual stored Path.
                var siblingNames = new HashSet<string>(
                    byPath.Values.Where(f => shape.ParentId(f) == parentTarget).Select(shape.Name), StringComparer.Ordinal);
                name = UniqueName(name, siblingNames);
                path = parentPath.Length == 0 ? "/" + name : parentPath + "/" + name;
                renamed++;
            }
            else created++;

            var id = existingIds.Contains(sourceId) ? Guid.NewGuid() : sourceId;
            var folder = shape.New(id);
            shape.Apply(folder, name, path, depth, parentTarget, createdBy);
            add(folder);
            byPath[path] = folder; existingIds.Add(id); folderMap[sourceId] = id;
            pathById[id] = path;
        }
        await db.SaveChangesAsync(ct);
        return new SectionRestoreResult(section, created, overwritten, skipped, renamed);
    }

    /// <summary>
    /// One parsed item of a by-name section: its conflict key, the backup's source id, and the two
    /// section-specific writes — apply onto the existing row, or materialize a new one under the
    /// (possibly renamed) name.
    /// </summary>
    private sealed record NamedRestoreItem<TEntity>(
        string Name,
        Guid SourceId,
        Action<TEntity> Overwrite,
        Func<Guid, string, TEntity> Create);

    /// <summary>
    /// The by-name conflict algorithm every "named row" section shares: counters, the taken-name
    /// set, the Skip/Overwrite/Rename branch, the source-id collision remap (K3) and the section
    /// result. <paramref name="read"/> parses an item BEFORE the conflict check — a section may
    /// reject an item outright (decryption/validation) whatever the policy says, and it must see
    /// the item's original name.
    /// </summary>
    private async Task<SectionRestoreResult> RestoreNamedSectionAsync<TEntity>(
        RestoreState s,
        string section,
        IDictionary<string, TEntity> byName,
        Func<TEntity, Guid> idOf,
        HashSet<Guid> existingIds,
        IDictionary<Guid, Guid>? idMap,
        Action<TEntity> add,
        Func<JsonNode, NamedRestoreItem<TEntity>> read,
        CancellationToken ct,
        bool preserveSourceIds = true)
    {
        var policy = s.Policy(section);
        int created = 0, overwritten = 0, skipped = 0, renamed = 0;
        var takenNames = new HashSet<string>(byName.Keys, StringComparer.Ordinal);

        foreach (var node in Items(s.Reader, section))
        {
            var item = read(node!);
            var name = item.Name;

            if (byName.TryGetValue(name, out var existing))
            {
                if (policy == RestoreConflictPolicy.Skip)
                {
                    if (idMap is not null) idMap[item.SourceId] = idOf(existing);
                    skipped++; continue;
                }
                if (policy == RestoreConflictPolicy.Overwrite)
                {
                    item.Overwrite(existing);
                    if (idMap is not null) idMap[item.SourceId] = idOf(existing);
                    overwritten++; continue;
                }
                name = UniqueName(name, takenNames); renamed++;
            }
            else created++;

            takenNames.Add(name);
            var id = preserveSourceIds && !existingIds.Contains(item.SourceId) ? item.SourceId : Guid.NewGuid();
            var entity = item.Create(id, name);
            add(entity);
            byName[name] = entity; existingIds.Add(id);
            if (idMap is not null) idMap[item.SourceId] = id;
        }
        await db.SaveChangesAsync(ct);
        return new SectionRestoreResult(section, created, overwritten, skipped, renamed);
    }

    private Task<SectionRestoreResult> RestoreCredentialsAsync(RestoreState s, CancellationToken ct) =>
        RestoreNamedSectionAsync(
            s,
            BackupSections.Credentials,
            s.Credentials,
            credential => credential.Id,
            s.ExistingCredentialIds,
            s.CredentialMap,
            credential => db.Credentials.Add(credential),
            item =>
            {
                var sourceId = Gid(item["sourceId"]);
                var name = item["name"]!.GetValue<string>();
                var username = item["username"]?.GetValue<string>() ?? "";
                var domain = item["domain"]?.GetValue<string>();
                var expiresAt = DateTime.TryParse(
                    item["expiresAt"]?.GetValue<string>(), null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var exp) ? exp : (DateTime?)null;
                byte[] encrypted = EncryptedPasswordFor(item, s, name);
                return new NamedRestoreItem<Credential>(
                    name,
                    sourceId,
                    existing =>
                    {
                        existing.Username = username; existing.Domain = domain; existing.EncryptedPassword = encrypted;
                        existing.ExpiresAt = expiresAt;
                    },
                    (id, finalName) => new Credential
                    {
                        Id = id, Name = finalName, Username = username, Domain = domain,
                        EncryptedPassword = encrypted, ExpiresAt = expiresAt,
                    });
            },
            ct);

    private Task<SectionRestoreResult> RestoreMachinesAsync(RestoreState s, CancellationToken ct) =>
        RestoreNamedSectionAsync(
            s,
            BackupSections.Machines,
            s.Machines,
            machine => machine.Id,
            s.ExistingMachineIds,
            s.MachineMap,
            machine => db.ManagedMachines.Add(machine),
            item =>
            {
                var sourceId = Gid(item["sourceId"]);
                var name = item["name"]!.GetValue<string>();
                var hostname = item["hostname"]?.GetValue<string>() ?? "";
                var winRmPort = item["winRmPort"]?.GetValue<int>() ?? 5985;
                var useSsl = item["useSsl"]?.GetValue<bool>() ?? false;
                var tags = item["tags"]?.GetValue<string>();
                var credSource = GidN(item["defaultCredentialId"]);
                var credTarget = credSource is null ? (Guid?)null : s.ResolveCredential(credSource.Value);
                return new NamedRestoreItem<ManagedMachine>(
                    name,
                    sourceId,
                    existing =>
                    {
                        existing.Hostname = hostname; existing.WinRmPort = winRmPort; existing.UseSsl = useSsl;
                        existing.Tags = tags; existing.DefaultCredentialId = credTarget;
                    },
                    (id, finalName) => new ManagedMachine
                    {
                        Id = id, Name = finalName, Hostname = hostname, WinRmPort = winRmPort, UseSsl = useSsl,
                        Tags = tags, DefaultCredentialId = credTarget,
                    });
            },
            ct);

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
            // Remap the backed-up folderId onto its restored target; unknown/missing -> Root.
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
                    ApplyCustomActivityFields(existing, item, s.Protector);
                    existing.UpdatedAt = DateTime.UtcNow;
                    s.CustomActivityMap[sourceId] = existing.Id;
                    overwritten++; continue;
                }
                // Skip OR Rename: a custom activity's Key is embedded in every referencing workflow
                // (custom:<key> activityType + __customKey), so it cannot be safely renamed on
                // restore.
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
            ApplyCustomActivityFields(def, item, s.Protector);
            db.CustomActivityDefinitions.Add(def);
            s.CustomActivities[key] = def; s.ExistingCustomActivityIds.Add(id); s.CustomActivityMap[sourceId] = id;
        }
        await db.SaveChangesAsync(ct);
        return new SectionRestoreResult(BackupSections.CustomActivities, created, overwritten, skipped, renamed);
    }

    private static void ApplyCustomActivityFields(
        CustomActivityDefinition def,
        JsonNode item,
        PassphraseSecretProtector protector)
    {
        def.Name = item["name"]!.GetValue<string>();
        def.Description = item["description"]?.GetValue<string>();
        def.Icon = item["icon"]?.GetValue<string>() ?? "extension";
        def.Color = item["color"]?.GetValue<string>();
        def.ScriptTemplate = RestoreEncryptedOrLegacyPlaintext(
            item["scriptTemplate"], protector, "custom activity scriptTemplate");
        def.Engine = item["engine"]?.GetValue<string>() ?? "auto";
        def.RunsRemote = item["runsRemote"]?.GetValue<bool>() ?? false;
        def.Isolated = item["isolated"]?.GetValue<bool>() ?? false;
        def.MemoryLimitMb = item["memoryLimitMb"]?.GetValue<int>();
        def.MaxProcesses = item["maxProcesses"]?.GetValue<int>();
        def.DefaultTimeoutSeconds = item["defaultTimeoutSeconds"]?.GetValue<int>();
        def.SuccessExitCodes = item["successExitCodes"]?.GetValue<string>();
        def.InputParametersJson = item["inputParametersJson"] is null
            ? "[]"
            : RestoreEncryptedOrLegacyPlaintext(
                item["inputParametersJson"], protector, "custom activity inputParametersJson");
        def.OutputParametersJson = item["outputParametersJson"]?.GetValue<string>() ?? "[]";
        def.IsEnabled = item["isEnabled"]?.GetValue<bool>() ?? false;
        def.Version = item["version"]?.GetValue<int>() ?? 1;
    }

    private Task<SectionRestoreResult> RestoreWorkflowsAsync(RestoreState s, CancellationToken ct) =>
        RestoreNamedSectionAsync(
            s,
            BackupSections.Workflows,
            s.Workflows,
            workflow => workflow.Id,
            s.ExistingWorkflowIds,
            s.WorkflowMap,
            workflow => db.Workflows.Add(workflow),
            item =>
            {
                var sourceId = Gid(item["sourceId"]);
                var name = item["name"]!.GetValue<string>();
                var description = item["description"]?.GetValue<string>();
                var isEnabled = item["isEnabled"]?.GetValue<bool>() ?? false;
                var version = item["version"]?.GetValue<int>() ?? 1;
                // Absent in backups written before the column existed, which reads as unlimited.
                // Validated because restore writes the entity directly, bypassing the endpoint.
                var maxConcurrent = item["maxConcurrentExecutions"]?.GetValue<int?>();
                if (WorkflowConcurrency.Validate(maxConcurrent) is not null) maxConcurrent = null;
                var folderTarget = s.ResolveFolder(GidN(item["folderId"]) ?? SharedWorkflowFolder.RootFolderId)
                    ?? SharedWorkflowFolder.RootFolderId;
                var definitionJson = RestoreDefinitionJson(item["definition"], s);
                return new NamedRestoreItem<Workflow>(
                    name,
                    sourceId,
                    existing =>
                    {
                        if (existing.CheckedOutByUserId is not null)
                            throw new BackupRestoreException(
                                $"Restore aborted: workflow '{existing.Name}' is locked for editing. Publish, unlock, or force-unlock it before overwrite restore.");
                        var now = DateTime.UtcNow;
                        db.WorkflowVersions.Add(new WorkflowVersion
                        {
                            Id = Guid.NewGuid(),
                            WorkflowId = existing.Id,
                            Version = existing.Version,
                            Name = existing.Name,
                            Description = existing.Description,
                            DefinitionJson = versionDefinitions.Protect(existing.DefinitionJson),
                            CreatedAt = now,
                            CreatedBy = existing.UpdatedBy ?? existing.CreatedBy ?? "restore",
                            ChangeNote = "Superseded by system backup restore",
                        });

                        existing.Description = description;
                        existing.DefinitionJson = definitionJson;
                        existing.Version = checked(existing.Version + 1);
                        existing.IsEnabled = isEnabled;
                        existing.MaxConcurrentExecutions = maxConcurrent;
                        existing.FolderId = folderTarget;
                        existing.UpdatedAt = now;
                        existing.UpdatedBy = "restore";
                        WorkflowMetadata.PopulateComputedColumns(existing);
                    },
                    (id, finalName) =>
                    {
                        var created = new Workflow
                        {
                            Id = id, Name = finalName, Description = description, DefinitionJson = definitionJson,
                            Version = Math.Max(1, version), IsEnabled = isEnabled, FolderId = folderTarget,
                            MaxConcurrentExecutions = maxConcurrent,
                            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                        };
                        WorkflowMetadata.PopulateComputedColumns(created);
                        return created;
                    });
            },
            ct);

    // Alerting rules carry no source-id map: a restored rule always gets a fresh id, and nothing
    // in the envelope references a rule by id.
    private Task<SectionRestoreResult> RestoreAlertingAsync(RestoreState s, CancellationToken ct) =>
        RestoreNamedSectionAsync(
            s,
            BackupSections.Alerting,
            s.NotificationRules,
            rule => rule.Id,
            s.ExistingNotificationRuleIds,
            idMap: null,
            rule => db.NotificationRules.Add(rule),
            item =>
            {
                var name = item["name"]!.GetValue<string>();
                var kind = Enum.TryParse<NotificationRuleKind>(item["kind"]?.GetValue<string>(), out var k) ? k : NotificationRuleKind.Custom;
                var isEnabled = item["isEnabled"]?.GetValue<bool>() ?? false;
                return new NamedRestoreItem<NotificationRule>(
                    name,
                    Guid.Empty,
                    existing =>
                    {
                        ApplyRuleScalars(existing, item, kind, isEnabled);
                        db.NotificationRoutes.RemoveRange(existing.Routes);
                        db.NotificationRuleTargets.RemoveRange(existing.Targets);
                        foreach (var r in RestoredRoutes(item, s, existing.Id)) db.NotificationRoutes.Add(r);
                        foreach (var tg in RestoredTargets(item, s, existing.Id)) db.NotificationRuleTargets.Add(tg);
                    },
                    (id, finalName) =>
                    {
                        var rule = new NotificationRule { Id = id, Name = finalName };
                        ApplyRuleScalars(rule, item, kind, isEnabled);
                        rule.Routes = RestoredRoutes(item, s, id);
                        rule.Targets = RestoredTargets(item, s, id);
                        return rule;
                    });
            },
            ct,
            preserveSourceIds: false);

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
        // A restored enabled System policy gets a fresh activation watermark so it never
        // back-alerts history.
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

    // Remaps scope targets onto restored folder/workflow ids; a target that resolves to nothing
    // (its
    // folder/workflow was not in the backup and doesn't exist here) is dropped with a warning —
    // targets are
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

    private JsonObject BuildRestoredSettings(BackupFileReader reader, PassphraseSecretProtector protector)
    {
        var runtimeJson = (reader.Sections[BackupSections.Settings] as JsonObject)?["runtimeJson"] as JsonObject
            ?? throw new BackupRestoreException(
                "Restore aborted: the settings section has no runtime settings payload.");
        var root = overrides.ReadOrEmpty();

        // Replace, don't merge: a restore reproduces the backup's override state while keeping
        // the target host's transient restart-marker bookkeeping.
        var keep = new HashSet<string>(
            runtimeJson.Select(kv => kv.Key).Append(RuntimeOverridesWriter.MetaSectionKey), StringComparer.Ordinal);
        foreach (var staleKey in root.Select(kv => kv.Key).Where(k => !keep.Contains(k)).ToList())
            root.Remove(staleKey);

        foreach (var (key, value) in runtimeJson)
        {
            if (key == RuntimeOverridesWriter.MetaSectionKey) continue;
            root[key] = value is null ? null : RewrapSettingValue(value, protector);
        }
        return root;
    }

    // ---- validation: confirm every referenced id resolves before any write happens (K12) ----

    private void ValidateReferences(RestoreState s)
    {
        var unresolved = new List<string>();

        // machines -> credentials
        foreach (var m in Items(s.Reader, BackupSections.Machines))
        {
            var c = GidN(m!["defaultCredentialId"]);
            if (c is not null && !s.CredentialResolvable(c.Value)) unresolved.Add($"machine '{m["name"]}' → credential {c}");
        }
        // workflows -> folder + definition refs
        foreach (var w in Items(s.Reader, BackupSections.Workflows))
        {
            var f = GidN(w!["folderId"]);
            if (f is not null && f != SharedWorkflowFolder.RootFolderId && !s.FolderResolvable(f.Value))
                unresolved.Add($"workflow '{w["name"]}' → folder {f}");
            if (w["definition"] is JsonNode def)
                foreach (var (kind, id) in ExtractDefinitionRefs(
                             WorkflowDefinitionSecretRewriter.UnsealBackupDefinition(def, s.Protector)))
                {
                    var ok = kind switch
                    {
                        "targetMachineId" => s.MachineResolvable(id),
                        "credentialId" => s.CredentialResolvable(id),
                        "__customDefinitionId" => s.CustomActivityResolvable(id),
                        _ => false,
                    };
                    if (!ok) unresolved.Add($"workflow '{w["name"]}' → {kind} {id}");
                }
        }
        // folder structure -> parent
        var structure = (s.Reader.Sections[BackupSections.Folders] as JsonObject)?["structure"] as JsonArray ?? [];
        foreach (var fo in structure)
        {
            var p = GidN(fo!["parentFolderId"]);
            if (p is not null && p != SharedWorkflowFolder.RootFolderId && !s.FolderResolvable(p.Value))
                unresolved.Add($"folder '{fo["name"]}' → parent {p}");
        }
        // global-variable folder structure -> parent
        var gStructure = (s.Reader.Sections[BackupSections.GlobalVariableFolders] as JsonObject)?["structure"] as JsonArray ?? [];
        foreach (var fo in gStructure)
        {
            var p = GidN(fo!["parentFolderId"]);
            if (p is not null && p != GlobalVariableFolder.RootFolderId && !s.GlobalFolderResolvable(p.Value))
                unresolved.Add($"global-variable folder '{fo["name"]}' → parent {p}");
        }
        // globals -> folder
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
        // Remap custom-activity node references (config.__customDefinitionId) onto their restored
        // ids.
        // Custom activities restore before workflows, so the map is complete here. Handles the
        // overwrite-merge case where the live id differs from the backed-up source id; a no-op for
        // a
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
    /// Reverses the backup's <c>$enc</c> wrapping for a runtime-settings value and re-seals it in
    /// the
    /// <c>enc:v1:</c> at-rest form (K9) so secrets never land in <c>appsettings.runtime.json</c> as
    /// plaintext. The EncryptingJsonConfigurationProvider transparently decrypts these on next
    /// load.
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

    /// <summary>
    /// Remaps the authoritative custom-activity reference only on custom workflow nodes. A
    /// same-named nested child parameter is application data and must remain untouched.
    /// </summary>
    private static void RemapCustomActivityRefs(JsonNode node, RestoreState s)
    {
        if (node is not JsonObject root || root["nodes"] is not JsonArray nodes) return;
        foreach (var candidate in nodes)
        {
            if (candidate is not JsonObject nodeObject || nodeObject["data"] is not JsonObject data)
                continue;
            var activityType = NodeActivityType(nodeObject, data);
            if (!NodePilot.Core.Activities.CustomActivityType.IsCustomType(activityType)
                || data["config"] is not JsonObject config
                || config["__customDefinitionId"] is not JsonValue idValue
                || !idValue.TryGetValue(out string? idString)
                || !Guid.TryParse(idString, out var sourceId)) continue;

            var target = s.ResolveCustomActivity(sourceId)
                ?? throw new BackupRestoreException(
                    $"Workflow custom activity reference {sourceId} is not resolvable.");
            config["__customDefinitionId"] = target.ToString();
        }
    }

    private static IEnumerable<(string kind, Guid id)> ExtractDefinitionRefs(JsonNode node)
    {
        if (node is not JsonObject root || root["nodes"] is not JsonArray nodes) yield break;
        foreach (var candidate in nodes)
        {
            if (candidate is not JsonObject nodeObject || nodeObject["data"] is not JsonObject data)
                continue;
            if (TryGuid(data["targetMachineId"], out var machineId))
                yield return ("targetMachineId", machineId);
            if (TryGuid(data["credentialId"], out var credentialId))
                yield return ("credentialId", credentialId);

            var activityType = NodeActivityType(nodeObject, data);
            if (NodePilot.Core.Activities.CustomActivityType.IsCustomType(activityType)
                && data["config"] is JsonObject config
                && TryGuid(config["__customDefinitionId"], out var definitionId))
            {
                yield return ("__customDefinitionId", definitionId);
            }
        }
    }

    private static string? NodeActivityType(JsonObject node, JsonObject data)
        => data["activityType"]?.GetValue<string>() ?? node["type"]?.GetValue<string>();

    private static bool TryGuid(JsonNode? node, out Guid id)
    {
        id = default;
        return node is JsonValue value
               && value.TryGetValue(out string? text)
               && Guid.TryParse(text, out id);
    }

    private static string? DecryptField(JsonNode? field, PassphraseSecretProtector protector)
    {
        if (field is JsonObject obj && obj.TryGetPropertyValue(WorkflowDefinitionSecretRewriter.EncKey, out var b64)
            && b64 is JsonValue v && v.TryGetValue(out string? s) && s is not null)
            return protector.Unprotect(Convert.FromBase64String(s));
        return null;
    }

    private static string RestoreEncryptedOrLegacyPlaintext(
        JsonNode? field,
        PassphraseSecretProtector protector,
        string fieldName)
    {
        if (field is null) return string.Empty;
        if (field is JsonValue value && value.TryGetValue(out string? plaintext))
            return plaintext ?? string.Empty; // v1/v2 compatibility

        var decrypted = DecryptField(field, protector);
        if (decrypted is not null) return decrypted;
        throw new BackupRestoreException($"Backup field '{fieldName}' is malformed.");
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
