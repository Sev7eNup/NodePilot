using System.Security.Claims;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;

namespace NodePilot.Core.Interfaces;

/// <summary>
/// Operations a user might want to perform on a workflow-shaped resource. Used by
/// <see cref="IResourceAuthorizationService.CanAccessAsync"/> as the requested-permission
/// argument so callers do not have to think about which <see cref="SharedFolderRole"/>
/// implies which operation — the mapping lives inside the service.
/// </summary>
public enum ResourceOp
{
    /// <summary>List + GET workflow / folder. Lowest bar.</summary>
    Read = 0,
    /// <summary>Execute / cancel / retry / resume executions.</summary>
    Run = 1,
    /// <summary>Create / update / delete / lock / publish / move / import.</summary>
    Edit = 2,
    /// <summary>Grant / revoke folder permissions on this folder.</summary>
    Admin = 3,
}

/// <summary>
/// What a specific user can do with a specific workflow or folder, encoded as boolean
/// flags so DTOs can ship the answer to the UI in one round-trip. The UI no longer
/// infers capabilities from the global role — it reads what the backend says.
/// <para>
/// <b>CanDelete</b> is its own flag because workflow-DELETE is gated on global Admin
/// role at the controller (<c>[Authorize(Roles = "Admin")]</c>), independently of the
/// folder-RBAC <c>CanEdit</c>. A folder-Editor Operator has <c>CanEdit=true</c> but
/// <c>CanDelete=false</c>; the UI must hide the Delete button accordingly.
/// </para>
/// </summary>
public record ResourceCapabilities(bool CanRead, bool CanRun, bool CanEdit, bool CanDelete, bool CanAdmin)
{
    public static readonly ResourceCapabilities None = new(false, false, false, false, false);
    public static readonly ResourceCapabilities All = new(true, true, true, true, true);
}

/// <summary>
/// Authoritative permission gate for workflow-shaped resources. Every API endpoint that
/// touches a workflow or folder must consult this service after the DB lookup — the
/// service combines (a) the global <see cref="UserRole"/>, (b) inherited
/// <see cref="SharedFolderPermission"/> grants on the folder ancestry, and (c) — once a
/// planned follow-up phase (internally tracked as "PR2 Stage B") ships — explicit
/// per-resource shares.
/// <para>
/// Implementations are expected to be scoped per request and to cache lookups for the
/// duration of that request: a list endpoint with 1000 workflows resolves the user's
/// accessible folder set once, then does only set-membership tests per row.
/// </para>
/// </summary>
public interface IResourceAuthorizationService
{
    /// <summary>
    /// Returns true when the given principal may perform <paramref name="op"/> on a
    /// workflow that lives in <paramref name="folderId"/>. Encapsulates the global-Admin
    /// bypass and the role-implies-role-implies-... ladder.
    /// </summary>
    Task<bool> CanAccessWorkflowAsync(ClaimsPrincipal user, Guid folderId, ResourceOp op, CancellationToken ct = default);

    /// <summary>
    /// Same shape, but for a folder-typed resource (used by the folder CRUD + permission
    /// endpoints). Folder-Read = list-children/get-folder; Folder-Edit = create/rename/
    /// move/delete the folder; Folder-Admin = grant/revoke permissions on it. Folder-Run
    /// is not meaningful and is always treated as <see cref="ResourceOp.Edit"/> here.
    /// </summary>
    Task<bool> CanAccessFolderAsync(ClaimsPrincipal user, Guid folderId, ResourceOp op, CancellationToken ct = default);

    /// <summary>
    /// All folder ids the principal can at least read, including inherited grants.
    /// Returned as a set so list endpoints can do <c>WHERE FolderId IN (...)</c> filtering
    /// efficiently. Returns "all folders" for global-Admin (the caller must not enumerate
    /// the set as a literal IN-clause for that case — instead bypass the filter).
    /// </summary>
    Task<AccessibleFolderSet> GetAccessibleFolderIdsAsync(ClaimsPrincipal user, CancellationToken ct = default);

    /// <summary>
    /// Computes the four capability flags for a workflow at <paramref name="folderId"/>.
    /// Used by DTO builders so list/detail responses can ship per-row capabilities to
    /// the UI without a separate roundtrip.
    /// </summary>
    Task<ResourceCapabilities> GetWorkflowCapabilitiesAsync(ClaimsPrincipal user, Guid folderId, CancellationToken ct = default);

    /// <summary>Same for a folder-typed resource.</summary>
    Task<ResourceCapabilities> GetFolderCapabilitiesAsync(ClaimsPrincipal user, Guid folderId, CancellationToken ct = default);

    /// <summary>
    /// Resolves the highest <see cref="SharedFolderRole"/> the principal holds on the
    /// folder via direct grant or ancestor inheritance. Returns null when the user has
    /// no grant on the chain. Global-Admin returns <see cref="SharedFolderRole.FolderAdmin"/>.
    /// </summary>
    Task<SharedFolderRole?> GetEffectiveFolderRoleAsync(ClaimsPrincipal user, Guid folderId, CancellationToken ct = default);

    /// <summary>
    /// Drops every per-request cache entry. Mutating endpoints (folder create/move/delete,
    /// grant/revoke) call this after the mutation lands so any subsequent capability lookup
    /// in the same request reflects the new state — without it, the response DTO ships
    /// pre-mutation capabilities that don't match what the user just did.
    /// </summary>
    void InvalidateAll();
}

/// <summary>
/// Result of <see cref="IResourceAuthorizationService.GetAccessibleFolderIdsAsync"/>.
/// When <see cref="IsUnrestricted"/> is true (global-Admin), <see cref="FolderIds"/>
/// is empty and callers must skip the filter rather than pass it as an empty IN-clause
/// (which would return zero rows). Distinct from "user has no folder access" where
/// <see cref="IsUnrestricted"/> is false and <see cref="FolderIds"/> is empty.
/// </summary>
public sealed class AccessibleFolderSet
{
    public bool IsUnrestricted { get; init; }
    public HashSet<Guid> FolderIds { get; init; } = [];

    public static readonly AccessibleFolderSet Unrestricted = new() { IsUnrestricted = true };
    public static readonly AccessibleFolderSet None = new() { IsUnrestricted = false };
}
