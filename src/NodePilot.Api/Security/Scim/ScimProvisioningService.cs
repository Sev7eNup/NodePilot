using System.Text.Json;
using System.Text.RegularExpressions;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NodePilot.Api.Security;
using NodePilot.Api.Security.Oidc;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Api.Security.Scim;

public sealed partial class ScimProvisioningService(
    NodePilotDbContext db,
    IOptions<ScimOptions> scimOptions,
    IOptions<EnterpriseOidcOptions> oidcOptions,
    IOptions<AuthenticationPolicyOptions> authenticationPolicy,
    IAuditStager auditStager,
    IMemoryCache? userStateCache = null,
    IHttpContextAccessor? httpContextAccessor = null,
    NodePilot.Core.Interfaces.IWorkflowEngine? workflowEngine = null,
    ILogger<ScimProvisioningService>? logger = null)
{
    private const int MaximumPageSize = 100;
    // NodePilotDbContext is scoped and not thread-safe, so one SCIM service instance handles
    // one mutation at a time. This attempt-local collector lets deeply nested group/user
    // reconciliation register durable execution cancellations for post-commit signalling.
    private HashSet<Guid>? executionIdsToSignal;

    public Task<ScimServiceResult<ScimUserResource>> CreateUserAsync(
        ScimUserWriteRequest request,
        string baseUrl,
        CancellationToken ct) => ExecuteSecurityMutationAsync(
            () => CreateUserCoreAsync(request, baseUrl, ct),
            result => Build(
                result.Created ? AuditActions.UserScimProvisioned : AuditActions.UserScimUpdated,
                result.Value),
            ct);

    private async Task<ScimServiceResult<ScimUserResource>> CreateUserCoreAsync(
        ScimUserWriteRequest request,
        string baseUrl,
        CancellationToken ct)
    {
        var validation = ValidateUserWrite(request, requireExternalId: true);
        if (validation is not null) return ScimServiceResult<ScimUserResource>.Fail(400, validation, "invalidValue");
        var authority = GetAuthority();
        if (authority is null)
            return ScimServiceResult<ScimUserResource>.Fail(503, "SCIM identity authority is not configured.");
        if (!await BreakGlassAccountPolicy.ExistsAsync(db, ct))
            return ScimServiceResult<ScimUserResource>.Fail(409,
                "A local break-glass administrator must be bootstrapped before external provisioning.",
                "mutability");

        var subject = request.ExternalId!;
        var identityCandidates = await db.ExternalIdentities
            .Include(x => x.User)
            .Where(x => x.Authority == authority && x.Subject == subject)
            .Take(3)
            .ToListAsync(ct);
        var existingIdentity = identityCandidates.SingleOrDefault(x =>
            string.Equals(x.Authority, authority, StringComparison.Ordinal)
            && string.Equals(x.Subject, subject, StringComparison.Ordinal));
        if (existingIdentity is null && identityCandidates.Count > 0)
            return ScimServiceResult<ScimUserResource>.Fail(409,
                "Identity differs only under the database collation and cannot be linked safely.", "uniqueness");
        if (existingIdentity is not null)
        {
            if (existingIdentity.User.IsTombstoned)
                return ScimServiceResult<ScimUserResource>.Fail(409,
                    "The external identity is tombstoned and requires explicit administrator reactivation.",
                    "uniqueness");
            var update = await UpdateExistingUserAsync(existingIdentity.User, request, ct);
            if (update is not null) return ScimServiceResult<ScimUserResource>.Fail(409, update, "uniqueness");
            return ScimServiceResult<ScimUserResource>.Ok(ToUserResource(existingIdentity, baseUrl));
        }

        var username = request.UserName!.Trim();
        if (await db.Users.AnyAsync(x => x.Username == username, ct))
            return ScimServiceResult<ScimUserResource>.Fail(409,
                "userName is already assigned to another identity.", "uniqueness");

        var now = DateTime.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = null,
            Provider = AuthProvider.Oidc,
            ExternalId = subject,
            Role = UserRole.Viewer,
            IsActive = request.Active ?? true,
            CreatedAt = now,
            PasswordChangedAt = now,
            LastDirectorySyncAt = now,
            DirectorySyncStatus = "ScimCurrent",
        };
        var identity = new ExternalIdentity
        {
            Id = Guid.NewGuid(),
            User = user,
            UserId = user.Id,
            Authority = authority,
            Subject = subject,
            CreatedAt = now,
            LastSeenAt = now,
        };
        user.ExternalIdentities.Add(identity);
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
            return ScimServiceResult<ScimUserResource>.Ok(ToUserResource(identity, baseUrl), created: true);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var racedCandidates = await db.ExternalIdentities
                .Include(x => x.User)
                .Where(x => x.Authority == authority && x.Subject == subject)
                .Take(3)
                .ToListAsync(ct);
            var raced = racedCandidates.SingleOrDefault(x =>
                string.Equals(x.Authority, authority, StringComparison.Ordinal)
                && string.Equals(x.Subject, subject, StringComparison.Ordinal));
            return raced is null
                ? ScimServiceResult<ScimUserResource>.Fail(409, "Identity provisioning conflict.", "uniqueness")
                : ScimServiceResult<ScimUserResource>.Ok(ToUserResource(raced, baseUrl));
        }
    }

    public async Task<ScimServiceResult<ScimUserResource>> GetUserAsync(Guid id, string baseUrl, CancellationToken ct)
    {
        var identity = await FindIdentityByUserIdAsync(id, ct);
        return identity is null || identity.User.IsTombstoned
            ? ScimServiceResult<ScimUserResource>.Fail(404, "User not found.")
            : ScimServiceResult<ScimUserResource>.Ok(ToUserResource(identity, baseUrl));
    }

    public async Task<ScimServiceResult<ScimListResponse<ScimUserResource>>> ListUsersAsync(
        string? filter,
        int startIndex,
        int count,
        string baseUrl,
        CancellationToken ct)
    {
        var authority = GetAuthority();
        if (authority is null)
            return ScimServiceResult<ScimListResponse<ScimUserResource>>.Fail(503,
                "SCIM identity authority is not configured.");
        var parsed = ParseFilter(filter, "userName", "externalId");
        if (parsed.Invalid)
            return ScimServiceResult<ScimListResponse<ScimUserResource>>.Fail(400,
                "Only userName eq and externalId eq filters are supported.", "invalidFilter");

        var query = db.ExternalIdentities.AsNoTracking()
            .Include(x => x.User)
            .Where(x => x.Authority == authority && !x.User.IsTombstoned);
        if (parsed.Attribute == "userName") query = query.Where(x => x.User.Username == parsed.Value);
        if (parsed.Attribute == "externalId") query = query.Where(x => x.Subject == parsed.Value);

        var total = await query.CountAsync(ct);
        var index = Math.Max(1, startIndex);
        var take = Math.Clamp(count, 0, MaximumPageSize);
        var rows = take == 0
            ? []
            : await query.OrderBy(x => x.User.Username)
                .Skip(index - 1).Take(take).ToListAsync(ct);
        var response = new ScimListResponse<ScimUserResource>
        {
            TotalResults = total,
            StartIndex = index,
            ItemsPerPage = rows.Count,
            Resources = rows.Select(x => ToUserResource(x, baseUrl)).ToList(),
        };
        return ScimServiceResult<ScimListResponse<ScimUserResource>>.Ok(response);
    }

    public Task<ScimServiceResult<ScimUserResource>> ReplaceUserAsync(
        Guid id,
        ScimUserWriteRequest request,
        string baseUrl,
        CancellationToken ct) => ExecuteSecurityMutationAsync(
            () => ReplaceUserCoreAsync(id, request, baseUrl, ct),
            result => Build(AuditActions.UserScimUpdated, result.Value), ct);

    private async Task<ScimServiceResult<ScimUserResource>> ReplaceUserCoreAsync(
        Guid id,
        ScimUserWriteRequest request,
        string baseUrl,
        CancellationToken ct)
    {
        var validation = ValidateUserWrite(request, requireExternalId: false);
        if (validation is not null) return ScimServiceResult<ScimUserResource>.Fail(400, validation, "invalidValue");
        var identity = await FindIdentityByUserIdAsync(id, ct);
        if (identity is null || identity.User.IsTombstoned)
            return ScimServiceResult<ScimUserResource>.Fail(404, "User not found.");
        if (!string.IsNullOrWhiteSpace(request.ExternalId)
            && !string.Equals(identity.Subject, request.ExternalId, StringComparison.Ordinal))
        {
            return ScimServiceResult<ScimUserResource>.Fail(400,
                "externalId is immutable because it is the OIDC subject.", "mutability");
        }
        var error = await UpdateExistingUserAsync(identity.User, request, ct);
        return error is not null
            ? ScimServiceResult<ScimUserResource>.Fail(409, error, "uniqueness")
            : ScimServiceResult<ScimUserResource>.Ok(ToUserResource(identity, baseUrl));
    }

    public Task<ScimServiceResult<ScimUserResource>> PatchUserAsync(
        Guid id,
        ScimPatchRequest request,
        string baseUrl,
        CancellationToken ct) => ExecuteSecurityMutationAsync(
            () => PatchUserCoreAsync(id, request, baseUrl, ct),
            result => Build(AuditActions.UserScimUpdated, result.Value), ct);

    private async Task<ScimServiceResult<ScimUserResource>> PatchUserCoreAsync(
        Guid id,
        ScimPatchRequest request,
        string baseUrl,
        CancellationToken ct)
    {
        if (request.Schemas?.Contains(ScimSchemas.Patch, StringComparer.Ordinal) != true
            || request.Operations is not { Count: > 0 and <= 20 })
        {
            return ScimServiceResult<ScimUserResource>.Fail(400, "Invalid SCIM PatchOp request.", "invalidSyntax");
        }
        var identity = await FindIdentityByUserIdAsync(id, ct);
        if (identity is null || identity.User.IsTombstoned)
            return ScimServiceResult<ScimUserResource>.Fail(404, "User not found.");

        var username = identity.User.Username;
        var active = identity.User.IsActive;
        foreach (var op in request.Operations)
        {
            if (!string.Equals(op.Op, "replace", StringComparison.OrdinalIgnoreCase))
                return ScimServiceResult<ScimUserResource>.Fail(400,
                    "User patches support replace only.", "invalidValue");
            if (string.IsNullOrWhiteSpace(op.Path) && op.Value.ValueKind == JsonValueKind.Object)
            {
                if (op.Value.TryGetProperty("active", out var objectActive)
                    && TryBoolean(objectActive, out var objectActiveValue)) active = objectActiveValue;
                if (op.Value.TryGetProperty("userName", out var objectUsername)
                    && objectUsername.ValueKind == JsonValueKind.String) username = objectUsername.GetString()!;
            }
            else if (string.Equals(op.Path, "active", StringComparison.OrdinalIgnoreCase)
                && TryBoolean(op.Value, out var activeValue)) active = activeValue;
            else if (string.Equals(op.Path, "userName", StringComparison.OrdinalIgnoreCase)
                     && op.Value.ValueKind == JsonValueKind.String) username = op.Value.GetString()!;
            else
                return ScimServiceResult<ScimUserResource>.Fail(400,
                    "Only active and userName can be patched.", "invalidPath");
        }

        var update = new ScimUserWriteRequest { UserName = username, Active = active };
        var error = await UpdateExistingUserAsync(identity.User, update, ct);
        return error is not null
            ? ScimServiceResult<ScimUserResource>.Fail(409, error, "uniqueness")
            : ScimServiceResult<ScimUserResource>.Ok(ToUserResource(identity, baseUrl));
    }

    public Task<ScimServiceResult<bool>> DeleteUserAsync(Guid id, CancellationToken ct) =>
        ExecuteSecurityMutationAsync(
            () => DeleteUserCoreAsync(id, ct),
            result => result.Succeeded
                ? auditStager.Build(
                    AuditActions.UserScimDeprovisioned, ScimActor, "User", id)
                : null,
            ct);

    private async Task<ScimServiceResult<bool>> DeleteUserCoreAsync(Guid id, CancellationToken ct)
    {
        var identity = await FindIdentityByUserIdAsync(id, ct);
        if (identity is null) return ScimServiceResult<bool>.Ok(true);
        if (identity.User.IsTombstoned) return ScimServiceResult<bool>.Ok(true);
        if (identity.User.Role == UserRole.Admin
            && await db.Users.CountAsync(x => x.IsActive && x.Role == UserRole.Admin, ct) <= 1)
        {
            return ScimServiceResult<bool>.Fail(409, "The last active administrator cannot be deactivated.");
        }
        var now = DateTime.UtcNow;
        identity.User.IsActive = false;
        identity.User.IsTombstoned = true;
        identity.User.SecurityStamp++;
        identity.User.LastDirectorySyncAt = now;
        identity.User.DirectorySyncStatus = "ScimTombstoned";
        db.DirectoryMemberships.RemoveRange(
            await db.DirectoryMemberships.Where(x => x.UserId == id).ToListAsync(ct));
        await RevokeSessionsAsync([id], now, ct);
        await db.SaveChangesAsync(ct);
        if (userStateCache is not null)
            UserSessionInvalidation.InvalidateUserStateCache(userStateCache, id);
        return ScimServiceResult<bool>.Ok(true);
    }

    public Task<ScimServiceResult<ScimGroupResource>> CreateGroupAsync(
        ScimGroupWriteRequest request,
        string baseUrl,
        CancellationToken ct) => ExecuteSecurityMutationAsync(
            () => CreateGroupCoreAsync(request, baseUrl, ct),
            result => Build(
                result.Created ? AuditActions.ScimGroupProvisioned : AuditActions.ScimGroupUpdated,
                result.Value),
            ct);

    private async Task<ScimServiceResult<ScimGroupResource>> CreateGroupCoreAsync(
        ScimGroupWriteRequest request,
        string baseUrl,
        CancellationToken ct)
    {
        var validation = ValidateGroupWrite(request, requireExternalId: true);
        if (validation is not null) return ScimServiceResult<ScimGroupResource>.Fail(400, validation, "invalidValue");
        var authority = GetAuthority();
        if (authority is null)
            return ScimServiceResult<ScimGroupResource>.Fail(503, "SCIM identity authority is not configured.");

        var externalId = request.ExternalId!;
        var groupCandidates = await db.ScimGroups
            .Where(x => x.Authority == authority && x.ExternalId == externalId)
            .Take(3)
            .ToListAsync(ct);
        var existing = groupCandidates.SingleOrDefault(x =>
            string.Equals(x.Authority, authority, StringComparison.Ordinal)
            && string.Equals(x.ExternalId, externalId, StringComparison.Ordinal));
        if (existing is null && groupCandidates.Count > 0)
            return ScimServiceResult<ScimGroupResource>.Fail(409,
                "Group identity differs only under the database collation.", "uniqueness");
        if (existing is not null)
        {
            if (existing.IsTombstoned)
                return ScimServiceResult<ScimGroupResource>.Fail(409,
                    "The group is tombstoned; an administrator must reactivate it through /api/admin/scim-groups before provisioning can resume.", "uniqueness");
            var replace = await ReplaceGroupMembersAsync(existing, request.DisplayName!, request.Members ?? [], ct);
            return replace is not null
                ? ScimServiceResult<ScimGroupResource>.Fail(400, replace, "invalidValue")
                : ScimServiceResult<ScimGroupResource>.Ok(await ToGroupResourceAsync(existing, baseUrl, ct));
        }

        var now = DateTime.UtcNow;
        var group = new ScimGroup
        {
            Id = Guid.NewGuid(),
            Authority = authority,
            ExternalId = externalId,
            DisplayName = request.DisplayName!.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ScimGroups.Add(group);
        var memberError = await ReplaceGroupMembersAsync(group, group.DisplayName, request.Members ?? [], ct);
        if (memberError is not null)
            return ScimServiceResult<ScimGroupResource>.Fail(400, memberError, "invalidValue");
        try
        {
            await db.SaveChangesAsync(ct);
            return ScimServiceResult<ScimGroupResource>.Ok(
                await ToGroupResourceAsync(group, baseUrl, ct), created: true);
        }
        catch (DbUpdateException)
        {
            return ScimServiceResult<ScimGroupResource>.Fail(409, "Group provisioning conflict.", "uniqueness");
        }
    }

    public async Task<ScimServiceResult<ScimGroupResource>> GetGroupAsync(Guid id, string baseUrl, CancellationToken ct)
    {
        var authority = GetAuthority();
        var group = authority is null ? null : await db.ScimGroups.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.Authority == authority && !x.IsTombstoned, ct);
        return group is null
            ? ScimServiceResult<ScimGroupResource>.Fail(404, "Group not found.")
            : ScimServiceResult<ScimGroupResource>.Ok(await ToGroupResourceAsync(group, baseUrl, ct));
    }

    public async Task<ScimServiceResult<ScimListResponse<ScimGroupResource>>> ListGroupsAsync(
        string? filter,
        int startIndex,
        int count,
        string baseUrl,
        CancellationToken ct)
    {
        var authority = GetAuthority();
        if (authority is null)
            return ScimServiceResult<ScimListResponse<ScimGroupResource>>.Fail(503,
                "SCIM identity authority is not configured.");
        var parsed = ParseFilter(filter, "displayName", "externalId");
        if (parsed.Invalid)
            return ScimServiceResult<ScimListResponse<ScimGroupResource>>.Fail(400,
                "Only displayName eq and externalId eq filters are supported.", "invalidFilter");

        var query = db.ScimGroups.AsNoTracking()
            .Where(x => x.Authority == authority && !x.IsTombstoned);
        if (parsed.Attribute == "displayName") query = query.Where(x => x.DisplayName == parsed.Value);
        if (parsed.Attribute == "externalId") query = query.Where(x => x.ExternalId == parsed.Value);
        var total = await query.CountAsync(ct);
        var index = Math.Max(1, startIndex);
        var take = Math.Clamp(count, 0, MaximumPageSize);
        var groups = take == 0 ? [] : await query.OrderBy(x => x.DisplayName)
            .Skip(index - 1).Take(take).ToListAsync(ct);
        var resources = new List<ScimGroupResource>();
        foreach (var group in groups) resources.Add(await ToGroupResourceAsync(group, baseUrl, ct));
        return ScimServiceResult<ScimListResponse<ScimGroupResource>>.Ok(
            new ScimListResponse<ScimGroupResource>
            {
                TotalResults = total,
                StartIndex = index,
                ItemsPerPage = resources.Count,
                Resources = resources,
            });
    }

    public Task<ScimServiceResult<ScimGroupResource>> ReplaceGroupAsync(
        Guid id,
        ScimGroupWriteRequest request,
        string baseUrl,
        CancellationToken ct) => ExecuteSecurityMutationAsync(
            () => ReplaceGroupCoreAsync(id, request, baseUrl, ct),
            result => Build(AuditActions.ScimGroupUpdated, result.Value), ct);

    private async Task<ScimServiceResult<ScimGroupResource>> ReplaceGroupCoreAsync(
        Guid id,
        ScimGroupWriteRequest request,
        string baseUrl,
        CancellationToken ct)
    {
        var validation = ValidateGroupWrite(request, requireExternalId: false);
        if (validation is not null) return ScimServiceResult<ScimGroupResource>.Fail(400, validation, "invalidValue");
        var group = await FindGroupAsync(id, ct);
        if (group is null) return ScimServiceResult<ScimGroupResource>.Fail(404, "Group not found.");
        if (!string.IsNullOrWhiteSpace(request.ExternalId)
            && !string.Equals(group.ExternalId, request.ExternalId, StringComparison.Ordinal))
        {
            return ScimServiceResult<ScimGroupResource>.Fail(400,
                "externalId is immutable.", "mutability");
        }
        var error = await ReplaceGroupMembersAsync(group, request.DisplayName!, request.Members ?? [], ct);
        return error is not null
            ? ScimServiceResult<ScimGroupResource>.Fail(400, error, "invalidValue")
            : ScimServiceResult<ScimGroupResource>.Ok(await ToGroupResourceAsync(group, baseUrl, ct));
    }

    public Task<ScimServiceResult<ScimGroupResource>> PatchGroupAsync(
        Guid id,
        ScimPatchRequest request,
        string baseUrl,
        CancellationToken ct) => ExecuteSecurityMutationAsync(
            () => PatchGroupCoreAsync(id, request, baseUrl, ct),
            result => Build(AuditActions.ScimGroupUpdated, result.Value), ct);

    private async Task<ScimServiceResult<ScimGroupResource>> PatchGroupCoreAsync(
        Guid id,
        ScimPatchRequest request,
        string baseUrl,
        CancellationToken ct)
    {
        if (request.Schemas?.Contains(ScimSchemas.Patch, StringComparer.Ordinal) != true
            || request.Operations is not { Count: > 0 and <= 20 })
            return ScimServiceResult<ScimGroupResource>.Fail(400, "Invalid SCIM PatchOp request.", "invalidSyntax");
        var group = await FindGroupAsync(id, ct);
        if (group is null) return ScimServiceResult<ScimGroupResource>.Fail(404, "Group not found.");

        var currentMembers = await GetGroupMemberIdsAsync(group, ct);
        var desiredMembers = currentMembers.ToHashSet();
        var displayName = group.DisplayName;
        foreach (var operation in request.Operations)
        {
            var op = operation.Op?.ToLowerInvariant();
            if (string.Equals(operation.Path, "displayName", StringComparison.OrdinalIgnoreCase)
                && op == "replace" && operation.Value.ValueKind == JsonValueKind.String)
            {
                displayName = operation.Value.GetString()!;
                continue;
            }

            if (!IsMembersPath(operation.Path) || op is not ("add" or "remove" or "replace"))
                return ScimServiceResult<ScimGroupResource>.Fail(400,
                    "Only displayName and members patches are supported.", "invalidPath");
            var parsedMembers = op == "remove" && TryReadMemberPath(operation.Path, out var pathMember)
                ? [pathMember]
                : ReadMembers(operation.Value);
            if (parsedMembers is null)
                return ScimServiceResult<ScimGroupResource>.Fail(400, "Invalid members value.", "invalidValue");
            if (op == "replace") desiredMembers = parsedMembers.ToHashSet();
            else if (op == "add") desiredMembers.UnionWith(parsedMembers);
            else desiredMembers.ExceptWith(parsedMembers);
        }

        var members = desiredMembers.Select(x => new ScimMember { Value = x.ToString("D") }).ToList();
        var error = await ReplaceGroupMembersAsync(group, displayName, members, ct);
        return error is not null
            ? ScimServiceResult<ScimGroupResource>.Fail(400, error, "invalidValue")
            : ScimServiceResult<ScimGroupResource>.Ok(await ToGroupResourceAsync(group, baseUrl, ct));
    }

    public Task<ScimServiceResult<bool>> DeleteGroupAsync(Guid id, CancellationToken ct) =>
        ExecuteSecurityMutationAsync(
            () => DeleteGroupCoreAsync(id, ct),
            result => result.Succeeded
                ? auditStager.Build(
                    AuditActions.ScimGroupDeprovisioned, ScimActor, "ScimGroup", id)
                : null,
            ct);

    private async Task<ScimServiceResult<bool>> DeleteGroupCoreAsync(Guid id, CancellationToken ct)
    {
        var group = await FindGroupAsync(id, ct);
        if (group is null || group.IsTombstoned) return ScimServiceResult<bool>.Ok(true);
        var affected = await GetGroupMemberIdsAsync(group, ct);
        var now = DateTime.UtcNow;
        group.IsActive = false;
        group.IsTombstoned = true;
        group.UpdatedAt = now;
        var memberships = await db.DirectoryMemberships
            .Where(x => x.Authority == group.Authority
                     && x.GroupKey == group.ExternalId
                     && affected.Contains(x.UserId))
            .ToListAsync(ct);
        db.DirectoryMemberships.RemoveRange(memberships);
        var authorizationError = await UpdateAuthorizationAsync(
            affected, group.ExternalId, new HashSet<Guid>(), now, ct);
        if (authorizationError is not null)
            return ScimServiceResult<bool>.Fail(409, authorizationError);
        await db.SaveChangesAsync(ct);
        if (userStateCache is not null)
        {
            foreach (var userId in affected)
                UserSessionInvalidation.InvalidateUserStateCache(userStateCache, userId);
        }
        return ScimServiceResult<bool>.Ok(true);
    }

    private async Task<string?> UpdateExistingUserAsync(
        User user,
        ScimUserWriteRequest request,
        CancellationToken ct)
    {
        var username = request.UserName?.Trim() ?? user.Username;
        if (string.IsNullOrWhiteSpace(username) || username.Length > 100 || username.Any(char.IsControl))
            return "userName must contain 1 to 100 printable characters.";
        if (username != user.Username && await db.Users.AnyAsync(x => x.Username == username && x.Id != user.Id, ct))
            return "userName is already assigned to another identity.";
        var active = request.Active ?? user.IsActive;
        if (!active && user.Role == UserRole.Admin
            && await db.Users.CountAsync(x => x.IsActive && x.Role == UserRole.Admin, ct) <= 1)
            return "The last active administrator cannot be deactivated.";

        var changed = user.Username != username || user.IsActive != active;
        user.Username = username;
        // SCIM may deactivate immediately, but cannot silently override a local suspension.
        // Explicit administrator reactivation is required before a later active=true applies.
        user.IsActive = user.IsActive && active;
        user.Provider = AuthProvider.Oidc;
        // A SCIM User PUT confirms account attributes, not group membership. Do not
        // refresh authorization freshness here; only a group membership snapshot may do
        // that, otherwise idempotent user updates could keep revoked groups alive forever.
        if (string.IsNullOrEmpty(user.DirectorySyncStatus))
            user.DirectorySyncStatus = "ScimUserCurrent";
        if (changed)
        {
            user.SecurityStamp++;
            await RevokeSessionsAsync([user.Id], DateTime.UtcNow, ct);
        }
        await db.SaveChangesAsync(ct);
        if (userStateCache is not null)
            UserSessionInvalidation.InvalidateUserStateCache(userStateCache, user.Id);
        return null;
    }

    private async Task<string?> ReplaceGroupMembersAsync(
        ScimGroup group,
        string displayName,
        IReadOnlyCollection<ScimMember> requestedMembers,
        CancellationToken ct)
    {
        displayName = displayName.Trim();
        if (displayName.Length is < 1 or > 256 || displayName.Any(char.IsControl))
            return "displayName must contain 1 to 256 printable characters.";
        if (requestedMembers.Count > 10_000) return "A group may not exceed 10000 members.";

        var ids = new HashSet<Guid>();
        foreach (var member in requestedMembers)
        {
            if (!Guid.TryParse(member.Value, out var id)) return "Every member value must be a SCIM user id.";
            ids.Add(id);
        }
        var authority = GetAuthority()!;
        var validIds = await db.ExternalIdentities
            .Where(x => x.Authority == authority && ids.Contains(x.UserId) && !x.User.IsTombstoned)
            .Select(x => x.UserId).Distinct().ToListAsync(ct);
        if (validIds.Count != ids.Count) return "One or more group members do not exist in this SCIM authority.";

        var oldIds = await GetGroupMemberIdsAsync(group, ct);
        var affected = oldIds.Concat(ids).Distinct().ToList();
        var removedIds = oldIds.Where(x => !ids.Contains(x)).ToList();
        var addedIds = ids.Where(x => !oldIds.Contains(x)).ToList();
        var removedMemberships = await db.DirectoryMemberships
            .Where(x => x.Authority == authority
                     && x.GroupKey == group.ExternalId
                     && removedIds.Contains(x.UserId))
            .ToListAsync(ct);
        db.DirectoryMemberships.RemoveRange(removedMemberships);
        var now = DateTime.UtcNow;
        var retainedMemberships = await db.DirectoryMemberships
            .Where(x => x.Authority == authority
                     && x.GroupKey == group.ExternalId
                     && ids.Contains(x.UserId))
            .ToListAsync(ct);
        foreach (var membership in retainedMemberships)
            membership.LastSeenAt = now;
        db.DirectoryMemberships.AddRange(addedIds.Select(userId => new DirectoryMembership
        {
            UserId = userId,
            Authority = authority,
            GroupKey = group.ExternalId,
            LastSeenAt = now,
        }));
        group.DisplayName = displayName;
        group.UpdatedAt = now;
        var authorizationError = await UpdateAuthorizationAsync(
            affected, group.ExternalId, ids, now, ct);
        if (authorizationError is not null) return authorizationError;
        await db.SaveChangesAsync(ct);
        if (userStateCache is not null)
        {
            foreach (var userId in affected)
                UserSessionInvalidation.InvalidateUserStateCache(userStateCache, userId);
        }
        return null;
    }

    private async Task<string?> UpdateAuthorizationAsync(
        IReadOnlyCollection<Guid> userIds,
        string changedGroup,
        IReadOnlySet<Guid> newMemberIds,
        DateTime now,
        CancellationToken ct)
    {
        if (userIds.Count == 0) return null;
        var users = await db.Users.Where(x => userIds.Contains(x.Id)).ToListAsync(ct);
        var maxStaleness = Math.Clamp(authenticationPolicy.Value.MaxAuthorizationStalenessMinutes, 1, 15);
        var cutoff = now.AddMinutes(-maxStaleness);
        var membershipsBefore = await db.DirectoryMemberships.AsNoTracking()
            .Where(x => x.Authority == GetAuthority()
                     && userIds.Contains(x.UserId)
                     && x.LastSeenAt >= cutoff)
            .GroupBy(x => x.UserId)
            .ToDictionaryAsync(x => x.Key, x => x.Select(m => m.GroupKey).ToHashSet(), ct);
        var postRoles = new Dictionary<Guid, UserRole>();
        var changedUsers = new List<Guid>();
        var accessGroupChanged = oidcOptions.Value.AllowedGroupIds
            .Any(group => string.Equals(group, changedGroup, StringComparison.Ordinal));
        var roleGroupChanged = oidcOptions.Value.GlobalRoleMappings
            .Any(mapping => string.Equals(mapping.GroupId, changedGroup, StringComparison.Ordinal));
        var authorizationGroupChanged = accessGroupChanged || roleGroupChanged;
        foreach (var user in users)
        {
            var groups = membershipsBefore.GetValueOrDefault(user.Id) ?? [];
            var hadMembership = groups.Contains(changedGroup);
            groups.Remove(changedGroup);
            if (newMemberIds.Contains(user.Id)) groups.Add(changedGroup);
            var role = OidcIdentityMapper.ResolveRole(groups, oidcOptions.Value.GlobalRoleMappings);
            postRoles[user.Id] = role;
            if (user.Role != role
                || authorizationGroupChanged
                   && hadMembership != newMemberIds.Contains(user.Id))
                changedUsers.Add(user.Id);
        }

        var currentAdminCount = await db.Users.CountAsync(x => x.IsActive && x.Role == UserRole.Admin, ct);
        var currentAffectedAdmins = users.Count(x => x.IsActive && x.Role == UserRole.Admin);
        var postAffectedAdmins = users.Count(x => x.IsActive && postRoles[x.Id] == UserRole.Admin);
        if (currentAdminCount > 0 && currentAdminCount - currentAffectedAdmins + postAffectedAdmins == 0)
            return "The last active administrator cannot be demoted.";

        foreach (var user in users)
        {
            if (changedUsers.Contains(user.Id))
            {
                user.Role = postRoles[user.Id];
                user.SecurityStamp++;
            }
            if (authorizationGroupChanged)
            {
                // Compatibility/UI watermark only. Enforcement uses the per-membership
                // authority + LastSeenAt values through ExternalAuthorizationEvaluator.
                user.LastDirectorySyncAt = now;
                user.DirectorySyncStatus = "ScimCurrent";
            }
        }
        await RevokeSessionsAsync(changedUsers, now, ct);
        return null;
    }

    private async Task RevokeSessionsAsync(IReadOnlyCollection<Guid> userIds, DateTime now, CancellationToken ct)
    {
        var sessions = await db.AuthSessions
            .Where(x => userIds.Contains(x.UserId) && x.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var session in sessions) session.RevokedAt = now;

        var executionIds = await ExternalExecutionCancellation.CancelAsync(
            db,
            userIds,
            now,
            "scim-authorization-change",
            "Execution cancelled because its SCIM principal authorization changed.",
            ct);
        executionIdsToSignal?.UnionWith(executionIds);
    }

    private async Task<ExternalIdentity?> FindIdentityByUserIdAsync(Guid userId, CancellationToken ct)
    {
        var authority = GetAuthority();
        if (authority is null) return null;
        var identities = await db.ExternalIdentities
            .Include(x => x.User)
            .Where(x => x.UserId == userId)
            .ToListAsync(ct);
        return identities.SingleOrDefault(x =>
            string.Equals(x.Authority, authority, StringComparison.Ordinal));
    }

    private async Task<ScimGroup?> FindGroupAsync(Guid id, CancellationToken ct)
    {
        var authority = GetAuthority();
        if (authority is null) return null;
        var group = await db.ScimGroups
            .SingleOrDefaultAsync(x => x.Id == id && !x.IsTombstoned, ct);
        return group is not null
               && string.Equals(group.Authority, authority, StringComparison.Ordinal)
            ? group
            : null;
    }

    private async Task<List<Guid>> GetGroupMemberIdsAsync(ScimGroup group, CancellationToken ct)
    {
        var authority = GetAuthority()!;
        return await db.DirectoryMemberships
            .Where(x => x.Authority == authority && x.GroupKey == group.ExternalId)
            .Join(db.ExternalIdentities.Where(x => x.Authority == authority),
                membership => membership.UserId,
                identity => identity.UserId,
                (membership, identity) => membership.UserId)
            .Distinct().ToListAsync(ct);
    }

    private ScimUserResource ToUserResource(ExternalIdentity identity, string baseUrl)
        => new()
        {
            Id = identity.UserId.ToString("D"),
            ExternalId = identity.Subject,
            UserName = identity.User.Username,
            Active = identity.User.IsActive && !identity.User.IsTombstoned,
            Meta = new ScimMeta
            {
                ResourceType = "User",
                Created = identity.CreatedAt,
                LastModified = identity.User.LastDirectorySyncAt ?? identity.LastSeenAt,
                Location = $"{baseUrl}/Users/{identity.UserId:D}",
            },
        };

    private async Task<ScimGroupResource> ToGroupResourceAsync(ScimGroup group, string baseUrl, CancellationToken ct)
    {
        var ids = await GetGroupMemberIdsAsync(group, ct);
        var users = await db.Users.AsNoTracking().Where(x => ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Username, ct);
        return new ScimGroupResource
        {
            Id = group.Id.ToString("D"),
            ExternalId = group.ExternalId,
            DisplayName = group.DisplayName,
            Members = ids.Select(id => new ScimMember
            {
                Value = id.ToString("D"),
                Display = users.GetValueOrDefault(id),
                Reference = $"{baseUrl}/Users/{id:D}",
            }).ToList(),
            Meta = new ScimMeta
            {
                ResourceType = "Group",
                Created = group.CreatedAt,
                LastModified = group.UpdatedAt,
                Location = $"{baseUrl}/Groups/{group.Id:D}",
            },
        };
    }

    private async Task<ScimServiceResult<T>> ExecuteSecurityMutationAsync<T>(
        Func<Task<ScimServiceResult<T>>> mutation,
        Func<ScimServiceResult<T>, AuditLogEntry?> buildAudit,
        CancellationToken ct)
    {
        await using var adminMutation = await AdminAccountMutationGate.EnterLocalAsync(ct);
        if (db.Database.CurrentTransaction is not null)
            return await mutation();

        var strategy = db.Database.CreateExecutionStrategy();
        AuditLogEntry? committedAudit = null;
        IReadOnlyList<Guid> committedExecutionIds = [];
        var result = await strategy.ExecuteAsync(async () =>
        {
            // Every core method reloads its state. Clearing here makes a transient retry
            // independent of entities left tracked by the failed attempt.
            db.ChangeTracker.Clear();
            executionIdsToSignal = [];
            committedExecutionIds = [];
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, ct);
            await AdminAccountMutationGate.AcquireTransactionLockAsync(db, ct);
            try
            {
                var result = await mutation();
                if (result.Succeeded)
                {
                    var audit = buildAudit(result);
                    if (audit is not null)
                    {
                        db.AuditLog.Add(audit);
                        await db.SaveChangesAsync(ct);
                    }
                    await transaction.CommitAsync(ct);
                    committedAudit = audit;
                    committedExecutionIds = executionIdsToSignal.ToList();
                }
                else
                {
                    await transaction.RollbackAsync(ct);
                    db.ChangeTracker.Clear();
                }
                return result;
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        });

        // The audit row is committed in the same transaction as the SCIM mutation.
        // Forward it only after the execution strategy has completed so transient retries
        // cannot create duplicate SIEM/support-log events.
        if (committedAudit is not null)
            ForwardCommittedAudit(committedAudit);
        if (committedExecutionIds.Count > 0)
        {
            using var signalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await ExternalExecutionCancellation.SignalAfterCommitAsync(
                    workflowEngine,
                    committedExecutionIds,
                    "scim-authorization-change",
                    signalTimeout.Token,
                    logger);
            }
            catch (OperationCanceledException) when (signalTimeout.IsCancellationRequested)
            {
                logger?.LogError(
                    "Post-commit SCIM execution cancellation signal timed out for {Count} execution(s)",
                    committedExecutionIds.Count);
            }
        }
        executionIdsToSignal = null;
        return result;
    }

    private void ForwardCommittedAudit(AuditLogEntry entry)
    {
        NodePilot.Engine.EngineMetrics.AuditWrites.Add(1,
            new KeyValuePair<string, object?>("result", "success"));
        if (logger is null) return;

        try
        {
            using (logger.BeginScope(new Dictionary<string, object?>
            {
                ["support.event_type"] = "AUDIT",
                ["support.message"] = $"{entry.Action} user={entry.Username ?? "-"} resource={entry.ResourceType ?? "-"}/{entry.ResourceId?.ToString() ?? "-"} ip={entry.IpAddress ?? "-"}",
                ["event.action"] = entry.Action,
                ["event.category"] = "iam",
                ["event.kind"] = "event",
                ["event.outcome"] = "success",
                ["event.dataset"] = "nodepilot.audit",
                ["event.id"] = entry.Id.ToString(),
                ["event.original"] = entry.Details,
                ["user.id"] = entry.UserId?.ToString(),
                ["user.name"] = entry.Username,
                ["source.ip"] = entry.IpAddress,
                ["AuditResourceType"] = entry.ResourceType,
                ["AuditResourceId"] = entry.ResourceId?.ToString(),
                ["SupportLog"] = true,
            }))
            {
                logger.LogInformation(
                    "AUDIT {Action} user={UserName} resource={ResourceType}/{ResourceId} ip={RemoteIp}",
                    entry.Action, entry.Username ?? "-", entry.ResourceType ?? "-",
                    entry.ResourceId?.ToString() ?? "-", entry.IpAddress ?? "-");
            }
        }
        catch (Exception ex)
        {
            // Persistence already committed. A logging sink failure must not turn the
            // successful SCIM response into a client-visible failure/retry.
            logger.LogError(ex, "SCIM audit SIEM forwarding failed for audit {AuditId}", entry.Id);
        }
    }

    private AuditActor ScimActor => new(
        null,
        "scim",
        httpContextAccessor?.HttpContext?.Connection.RemoteIpAddress?.ToString());

    private AuditLogEntry? Build(string action, ScimUserResource? resource)
    {
        if (resource is null || !Guid.TryParse(resource.Id, out var id)) return null;
        return auditStager.Build(
            action,
            ScimActor,
            "User",
            id,
            AuditDetails.Json(("username", resource.UserName), ("active", resource.Active)));
    }

    private AuditLogEntry? Build(string action, ScimGroupResource? resource)
    {
        if (resource is null || !Guid.TryParse(resource.Id, out var id)) return null;
        return auditStager.Build(
            action,
            ScimActor,
            "ScimGroup",
            id,
            AuditDetails.Json(("displayName", resource.DisplayName), ("members", resource.Members.Count)));
    }

    private string? GetAuthority()
    {
        var oidcAuthority = oidcOptions.Value.Authority;
        var configuredAuthority = scimOptions.Value.Authority;
        var authority = string.IsNullOrWhiteSpace(configuredAuthority)
            ? oidcAuthority
            : configuredAuthority;
        return OidcIdentityMapper.IsValidIssuer(authority)
               && string.Equals(authority, oidcAuthority, StringComparison.Ordinal)
            ? authority
            : null;
    }

    private static string? ValidateUserWrite(ScimUserWriteRequest request, bool requireExternalId)
    {
        if (string.IsNullOrWhiteSpace(request.UserName)
            || request.UserName.Length > 100
            || request.UserName.Any(char.IsControl))
            return "userName must contain 1 to 100 printable characters.";
        if (requireExternalId && string.IsNullOrWhiteSpace(request.ExternalId))
            return "externalId is required and must equal the OIDC subject.";
        if (request.ExternalId is { Length: > 384 }
            || request.ExternalId?.Any(char.IsControl) == true
            || request.ExternalId is { } externalId
               && !string.Equals(externalId, externalId.Trim(), StringComparison.Ordinal))
            return "externalId is invalid.";
        return null;
    }

    private static string? ValidateGroupWrite(ScimGroupWriteRequest request, bool requireExternalId)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName)
            || request.DisplayName.Length > 256
            || request.DisplayName.Any(char.IsControl))
            return "displayName must contain 1 to 256 printable characters.";
        if (requireExternalId && string.IsNullOrWhiteSpace(request.ExternalId))
            return "externalId is required.";
        if (request.ExternalId is { Length: > 256 }
            || request.ExternalId?.Any(char.IsControl) == true
            || request.ExternalId is { } externalId
               && !string.Equals(externalId, externalId.Trim(), StringComparison.Ordinal))
            return "externalId is invalid.";
        return null;
    }

    private static (string? Attribute, string? Value, bool Invalid) ParseFilter(
        string? filter,
        params string[] supportedAttributes)
    {
        if (string.IsNullOrWhiteSpace(filter)) return (null, null, false);
        var match = ScimFilterRegex().Match(filter);
        if (!match.Success || !supportedAttributes.Contains(match.Groups[1].Value, StringComparer.OrdinalIgnoreCase))
            return (null, null, true);
        return (supportedAttributes.First(x => x.Equals(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase)),
            match.Groups[2].Value.Replace("\\\"", "\"", StringComparison.Ordinal), false);
    }

    private static bool TryBoolean(JsonElement value, out bool result)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            result = value.GetBoolean();
            return true;
        }
        result = false;
        return false;
    }

    private static bool IsMembersPath(string? path)
        => string.IsNullOrWhiteSpace(path)
           || path.Equals("members", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith("members[", StringComparison.OrdinalIgnoreCase);

    private static List<Guid>? ReadMembers(JsonElement value)
    {
        try
        {
            List<ScimMember>? members;
            if (value.ValueKind == JsonValueKind.Array)
            {
                members = value.Deserialize<List<ScimMember>>();
            }
            else if (value.ValueKind == JsonValueKind.Object
                     && value.TryGetProperty("members", out var nested))
            {
                members = nested.Deserialize<List<ScimMember>>();
            }
            else if (value.ValueKind == JsonValueKind.Object)
            {
                members = [value.Deserialize<ScimMember>()!];
            }
            else
            {
                members = null;
            }
            if (members is null) return null;
            var ids = new List<Guid>();
            foreach (var member in members)
            {
                if (!Guid.TryParse(member.Value, out var id)) return null;
                ids.Add(id);
            }
            return ids;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadMemberPath(string? path, out Guid memberId)
    {
        memberId = Guid.Empty;
        var match = ScimMemberPathRegex().Match(path ?? string.Empty);
        return match.Success && Guid.TryParse(match.Groups[1].Value, out memberId);
    }

    [GeneratedRegex("^\\s*([A-Za-z][A-Za-z0-9]*)\\s+eq\\s+\"((?:[^\"\\\\]|\\\\.)*)\"\\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex ScimFilterRegex();

    [GeneratedRegex("^members\\[value\\s+eq\\s+\"([0-9A-Fa-f-]{36})\"\\]$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex ScimMemberPathRegex();
}
