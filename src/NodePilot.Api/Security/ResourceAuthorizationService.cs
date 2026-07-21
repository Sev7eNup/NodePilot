using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Api.Security;

/// <summary>
/// Default implementation of <see cref="IResourceAuthorizationService"/> over the
/// <see cref="SharedWorkflowFolder"/> + <see cref="SharedFolderPermission"/> tables.
/// Scoped per request so the in-memory cache amortizes repeated lookups inside a single
/// list/detail endpoint without leaking permissions across requests.
/// <para>
/// Permission resolution: walks the folder ancestry from the requested folder up to
/// Root, collects every grant that targets the user (V1: only PrincipalType=User),
/// and takes the highest <see cref="SharedFolderRole"/>. Higher-role-implies-lower
/// is encoded in the private <c>OpRequiresRole</c> helper. Global-Admin bypasses the whole chain.
/// </para>
/// </summary>
public sealed class ResourceAuthorizationService : IResourceAuthorizationService
{
    private readonly NodePilotDbContext _db;

    // Per-request caches. Service is scoped, so these die at end-of-request.
    // Keyed on (userId, folderId): in single-principal-per-request use (the common case)
    // a userId-less key would still be safe, but the public API takes ClaimsPrincipal —
    // any caller switching principals mid-scope would otherwise read stale grants. The
    // cost of keying on a Guid pair is one extra hash bucket per lookup.
    private readonly Dictionary<(Guid userId, Guid folderId), List<SharedFolderPermission>> _ancestryGrantsCache = [];
    private readonly Dictionary<Guid, AccessibleFolderSet> _accessibleFolderSetCache = [];
    private readonly Dictionary<Guid, IReadOnlyCollection<DirectoryGroupPrincipal>> _directoryGroupCache = [];
    private List<SharedWorkflowFolder>? _allFoldersCache;

    public ResourceAuthorizationService(NodePilotDbContext db)
    {
        _db = db;
    }

    public async Task<bool> CanAccessWorkflowAsync(ClaimsPrincipal user, Guid folderId, ResourceOp op, CancellationToken ct = default)
    {
        if (IsGlobalAdmin(user)) return true;
        // Global role cap applies to authorization decisions too, not just capability
        // hints. Without this, a global Viewer who somehow holds FolderAdmin on a folder
        // could call grant/revoke endpoints (which only have [Authorize] + _authz, no
        // role-attribute fallback). The capability response already capped this for the
        // UI; mirror it on the actual auth decision so the API doesn't disagree with the
        // UI it just shipped to the same caller.
        if (!OpAllowedByGlobalRole(op, user)) return false;
        var role = await GetEffectiveFolderRoleAsync(user, folderId, ct);
        return role.HasValue && OpAllowedByRole(op, role.Value);
    }

    public Task<bool> CanAccessFolderAsync(ClaimsPrincipal user, Guid folderId, ResourceOp op, CancellationToken ct = default)
    {
        // Folder-shaped resource currently has the same permission semantics as the
        // workflow-shaped one — a user with FolderEditor on /finance can rename
        // /finance, create children, and edit workflows inside it. Run is not
        // meaningful for folders; treat as Edit so callers don't need to special-case.
        if (op == ResourceOp.Run) op = ResourceOp.Edit;
        return CanAccessWorkflowAsync(user, folderId, op, ct);
    }

    /// <summary>
    /// Mirrors <see cref="GlobalRoleCap"/>: a global Viewer can never edit/run/admin,
    /// a global Operator can never admin. Admin is short-circuited by the caller.
    /// </summary>
    private static bool OpAllowedByGlobalRole(ResourceOp op, ClaimsPrincipal user)
    {
        if (user.IsInRole(nameof(UserRole.Operator)))
            return op != ResourceOp.Admin;
        // Viewer (or no role): read-only.
        return op == ResourceOp.Read;
    }

    public async Task<AccessibleFolderSet> GetAccessibleFolderIdsAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        // Admin: unique sentinel key so the cache hits regardless of which Admin asks.
        if (IsGlobalAdmin(user))
        {
            return AccessibleFolderSet.Unrestricted;
        }

        var userId = TryGetUserId(user);
        if (userId is null) return AccessibleFolderSet.None;
        if (_accessibleFolderSetCache.TryGetValue(userId.Value, out var cached)) return cached;

        // Pull every folder + every grant for this user in two queries, then resolve
        // accessibility entirely in memory. Folder count in any realistic deployment is
        // bounded (depth 5 means thousands max even at flat-org-of-thousands scale); the
        // cost of the in-memory walk is negligible compared to per-row DB hits.
        var allFolders = await GetAllFoldersAsync(ct);
        var userKey = userId.Value.ToString("D");
        var directoryGroups = await GetDirectoryGroupsAsync(userId.Value, ct);
        var groupKeys = directoryGroups.Select(group => group.GroupKey).Distinct().ToList();
        var groupAuthorities = directoryGroups.Select(group => group.Authority).Distinct().ToList();
        if (groupAuthorities.Contains(ExternalIdentity.ActiveDirectoryAuthority, StringComparer.Ordinal))
            groupAuthorities.Add(string.Empty);
        // Group-aware grant lookup: include any (PrincipalType=Group, PrincipalKey IN
        // userGroupSids) grant alongside the per-user grants. Local users have an empty
        // groupSids list so this branch is a no-op for them.
        var candidateGrants = await _db.SharedFolderPermissions
            .AsNoTracking()
            .Where(p =>
                (p.PrincipalType == FolderPrincipalType.User && p.PrincipalKey == userKey)
                || (p.PrincipalType == FolderPrincipalType.Group
                    && groupKeys.Contains(p.PrincipalKey)
                    && groupAuthorities.Contains(p.PrincipalAuthority)))
            .ToListAsync(ct);
        var grants = candidateGrants
            .Where(permission => permission.PrincipalType == FolderPrincipalType.User
                              || directoryGroups.Any(group => group.Matches(permission)))
            .Select(permission => new { permission.FolderId, permission.Role })
            .ToList();

        if (grants.Count == 0)
        {
            _accessibleFolderSetCache[userId.Value] = AccessibleFolderSet.None;
            return AccessibleFolderSet.None;
        }

        // Each grant on a folder F implicitly grants access to F + all descendants of F.
        // Collect the descendant set per granted folder, union them all.
        var grantedFolderIds = grants.Select(g => g.FolderId).ToHashSet();
        var byParent = allFolders.GroupBy(f => f.ParentFolderId)
            .ToDictionary(g => g.Key ?? Guid.Empty, g => g.ToList());

        var accessible = new HashSet<Guid>();
        var stack = new Stack<Guid>(grantedFolderIds);
        while (stack.Count > 0)
        {
            var fid = stack.Pop();
            if (!accessible.Add(fid)) continue;
            if (byParent.TryGetValue(fid, out var children))
                foreach (var c in children) stack.Push(c.Id);
        }

        var result = new AccessibleFolderSet
        {
            IsUnrestricted = false,
            FolderIds = accessible,
        };
        _accessibleFolderSetCache[userId.Value] = result;
        return result;
    }

    /// <summary>
    /// Drop every cache entry. Call from controllers that mutate the folder tree or grant
    /// table mid-request — without this, capabilities computed before the mutation would be
    /// returned in the response, masking the just-applied change. Cheap: scoped service,
    /// caches are small per-request dictionaries.
    /// </summary>
    public void InvalidateAll()
    {
        _ancestryGrantsCache.Clear();
        _accessibleFolderSetCache.Clear();
        _directoryGroupCache.Clear();
        _allFoldersCache = null;
    }

    public async Task<ResourceCapabilities> GetWorkflowCapabilitiesAsync(ClaimsPrincipal user, Guid folderId, CancellationToken ct = default)
    {
        if (IsGlobalAdmin(user)) return ResourceCapabilities.All;

        var role = await GetEffectiveFolderRoleAsync(user, folderId, ct);
        if (!role.HasValue) return ResourceCapabilities.None;

        // Capabilities must AND with the user's *global* UserRole — controllers still gate
        // mutations / executions via [Authorize(Roles = "Admin,Operator")] (defense in depth),
        // so a global Viewer who happens to hold FolderOperator on a folder can read but
        // cannot run there: the API would 403 the run anyway, and the UI must reflect that
        // by hiding the "Run" button. Without this cap, capabilities lie to the UI and we'd
        // ship buttons that 403.
        var roleCap = GlobalRoleCap(user);
        // CanDelete follows CanEdit at the folder level, but is additionally gated by the
        // global Admin-only check in the controller — an Operator with FolderEditor sees
        // the workflow as editable, but Delete stays Admin-only ([Authorize(Roles="Admin")]).
        return new ResourceCapabilities(
            CanRead:   OpAllowedByRole(ResourceOp.Read,  role.Value) && roleCap.CanRead,
            CanRun:    OpAllowedByRole(ResourceOp.Run,   role.Value) && roleCap.CanRun,
            CanEdit:   OpAllowedByRole(ResourceOp.Edit,  role.Value) && roleCap.CanEdit,
            CanDelete: OpAllowedByRole(ResourceOp.Edit,  role.Value) && roleCap.CanDelete,
            CanAdmin:  OpAllowedByRole(ResourceOp.Admin, role.Value) && roleCap.CanAdmin);
    }

    /// <summary>
    /// Maximum capabilities each global <see cref="UserRole"/> can ever have, regardless of
    /// folder grants. This must mirror the <c>[Authorize(Roles=...)]</c> gates on the
    /// mutation/run endpoints so capabilities don't promise more than the API delivers.
    /// </summary>
    private static ResourceCapabilities GlobalRoleCap(ClaimsPrincipal user)
    {
        // Note: Admin is handled in the caller via the IsGlobalAdmin shortcut — never reaches here.
        if (user.IsInRole(nameof(UserRole.Operator)))
            return new ResourceCapabilities(CanRead: true, CanRun: true, CanEdit: true, CanDelete: false, CanAdmin: false);
        // Viewer (or no role at all): read-only, no matter how generous folder grants are.
        return new ResourceCapabilities(CanRead: true, CanRun: false, CanEdit: false, CanDelete: false, CanAdmin: false);
    }

    public Task<ResourceCapabilities> GetFolderCapabilitiesAsync(ClaimsPrincipal user, Guid folderId, CancellationToken ct = default)
        => GetWorkflowCapabilitiesAsync(user, folderId, ct);

    public async Task<SharedFolderRole?> GetEffectiveFolderRoleAsync(ClaimsPrincipal user, Guid folderId, CancellationToken ct = default)
    {
        if (IsGlobalAdmin(user)) return SharedFolderRole.FolderAdmin;
        var userId = TryGetUserId(user);
        if (userId is null) return null;

        var directoryGroups = await GetDirectoryGroupsAsync(userId.Value, ct);
        var grants = await GetAncestryGrantsAsync(folderId, userId.Value, directoryGroups, ct);
        if (grants.Count == 0) return null;

        // Highest role wins. Enum is ordered FolderViewer < FolderOperator < FolderEditor < FolderAdmin.
        return grants.Max(g => g.Role);
    }

    /// <summary>
    /// Returns every <see cref="SharedFolderPermission"/> that grants access to the
    /// folder at <paramref name="folderId"/> for <paramref name="userId"/> — including
    /// grants on any ancestor folder, and any authority-scoped group grant represented by
    /// <paramref name="directoryGroups"/>. Cached per (folderId, userId) tuple within the
    /// request scope (group-membership doesn't change mid-request).
    /// </summary>
    private async Task<List<SharedFolderPermission>> GetAncestryGrantsAsync(
        Guid folderId, Guid userId,
        IReadOnlyCollection<DirectoryGroupPrincipal> directoryGroups,
        CancellationToken ct)
    {
        var cacheKey = (userId, folderId);
        if (_ancestryGrantsCache.TryGetValue(cacheKey, out var cached)) return cached;

        // Build the ancestor chain folderId → ... → Root.
        var allFolders = await GetAllFoldersAsync(ct);
        var byId = allFolders.ToDictionary(f => f.Id);
        var chain = new List<Guid>();
        var current = folderId;
        var depth = 0;
        while (depth <= SharedWorkflowFolder.MaxDepth + 1 && byId.TryGetValue(current, out var folder))
        {
            chain.Add(folder.Id);
            if (folder.ParentFolderId is null) break;
            current = folder.ParentFolderId.Value;
            depth++;
        }

        if (chain.Count == 0)
        {
            _ancestryGrantsCache[cacheKey] = [];
            return _ancestryGrantsCache[cacheKey];
        }

        var userKey = userId.ToString("D");
        var groupKeys = directoryGroups.Select(group => group.GroupKey).Distinct().ToList();
        var groupAuthorities = directoryGroups.Select(group => group.Authority).Distinct().ToList();
        if (groupAuthorities.Contains(ExternalIdentity.ActiveDirectoryAuthority, StringComparer.Ordinal))
            groupAuthorities.Add(string.Empty);
        var candidateGrants = await _db.SharedFolderPermissions
            .AsNoTracking()
            .Where(p => chain.Contains(p.FolderId)
                     && ((p.PrincipalType == FolderPrincipalType.User && p.PrincipalKey == userKey)
                         || (p.PrincipalType == FolderPrincipalType.Group
                             && groupKeys.Contains(p.PrincipalKey)
                             && groupAuthorities.Contains(p.PrincipalAuthority))))
            .ToListAsync(ct);
        var grants = candidateGrants
            .Where(permission => permission.PrincipalType == FolderPrincipalType.User
                              || directoryGroups.Any(group => group.Matches(permission)))
            .ToList();
        _ancestryGrantsCache[cacheKey] = grants;
        return grants;
    }

    /// <summary>
    /// Loads the normalized directory-membership snapshot from the server-side database.
    /// Keeping this data out of the JWT prevents enterprise users with hundreds of groups
    /// from exceeding browser/proxy cookie limits and lets revocation take effect without
    /// waiting for the cookie to expire.
    /// </summary>
    private async Task<IReadOnlyCollection<DirectoryGroupPrincipal>> GetDirectoryGroupsAsync(
        Guid userId,
        CancellationToken ct)
    {
        if (_directoryGroupCache.TryGetValue(userId, out var cached)) return cached;
        var user = await _db.Users.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == userId, ct);
        if (user is null) return [];
        var groups = await DirectoryGroupPrincipal.LoadAsync(_db, user, ct);
        _directoryGroupCache[userId] = groups;
        return groups;
    }

    private async Task<List<SharedWorkflowFolder>> GetAllFoldersAsync(CancellationToken ct)
    {
        if (_allFoldersCache is not null) return _allFoldersCache;
        _allFoldersCache = await _db.SharedWorkflowFolders.AsNoTracking().ToListAsync(ct);
        return _allFoldersCache;
    }

    private static bool IsGlobalAdmin(ClaimsPrincipal user) => user.IsInRole(nameof(UserRole.Admin));

    private static Guid? TryGetUserId(ClaimsPrincipal user)
    {
        var idStr = user.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(idStr, out var id) ? id : null;
    }

    /// <summary>
    /// Maps an <see cref="ResourceOp"/> to the minimum <see cref="SharedFolderRole"/>
    /// that allows it. Higher roles imply all lower-role operations.
    /// </summary>
    private static bool OpAllowedByRole(ResourceOp op, SharedFolderRole role) => op switch
    {
        ResourceOp.Read  => role >= SharedFolderRole.FolderViewer,
        ResourceOp.Run   => role >= SharedFolderRole.FolderOperator,
        ResourceOp.Edit  => role >= SharedFolderRole.FolderEditor,
        ResourceOp.Admin => role >= SharedFolderRole.FolderAdmin,
        _ => false,
    };
}
