using System.Security.Claims;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;

namespace NodePilot.Core.Interfaces;

/// <summary>
/// Operations a user can request on a workflow-shaped resource. Passed to
/// <see cref="IResourceAuthorizationService.CanAccessAsync"/> as the requested permission; the
/// mapping from <see cref="SharedFolderRole"/> to operation lives inside the service.
/// </summary>
public enum ResourceOp
{
    /// <summary>List and read a workflow or folder. The lowest level.</summary>
    Read = 0,
    /// <summary>Execute / cancel / retry / resume executions.</summary>
    Run = 1,
    /// <summary>Create / update / delete / lock / publish / move / import.</summary>
    Edit = 2,
    /// <summary>Grant / revoke folder permissions on this folder.</summary>
    Admin = 3,
}

/// <summary>
/// What a user can do with one workflow or folder, as boolean flags, so DTOs can ship the
/// answer to the UI in one round-trip instead of the UI deriving it from the global role.
/// <para>
/// <c>CanDelete</c> is a separate flag because workflow DELETE and recursive folder DELETE are
/// gated on the global Admin role independently of folder-RBAC <c>CanEdit</c>. A folder-Editor
/// Operator has <c>CanEdit=true</c> but <c>CanDelete=false</c>, so the UI hides destructive
/// subtree deletion for them.
/// </para>
/// </summary>
public record ResourceCapabilities(bool CanRead, bool CanRun, bool CanEdit, bool CanDelete, bool CanAdmin)
{
    public static readonly ResourceCapabilities None = new(false, false, false, false, false);
    public static readonly ResourceCapabilities All = new(true, true, true, true, true);
}

/// <summary>
/// Authoritative permission gate for workflow-shaped resources. Every API endpoint that
/// touches a workflow or folder consults this service after the DB lookup; it combines the
/// global <see cref="UserRole"/> with inherited <see cref="SharedFolderPermission"/> grants
/// along the folder ancestry.
/// <para>
/// Implementations are scoped per request and cache lookups for that request: a list endpoint
/// resolves the accessible folder set once and then only tests set membership per row.
/// </para>
/// </summary>
public interface IResourceAuthorizationService
{
    /// <summary>
    /// Returns true when the principal may perform <paramref name="op"/> on a workflow that
    /// lives in <paramref name="folderId"/>. Covers the global-Admin bypass and the ladder of
    /// roles that imply weaker operations.
    /// </summary>
    Task<bool> CanAccessWorkflowAsync(ClaimsPrincipal user, Guid folderId, ResourceOp op, CancellationToken ct = default);

    /// <summary>
    /// Same shape for a folder-typed resource, used by the folder CRUD and permission endpoints.
    /// Folder-Read lists children and gets the folder; Folder-Edit creates, renames, moves or
    /// deletes an empty folder; Folder-Admin grants and revokes permissions on it. Recursive
    /// deletion has an additional global-Admin gate. Folder-Run has no meaning and is always
    /// treated as <see cref="ResourceOp.Edit"/>.
    /// </summary>
    Task<bool> CanAccessFolderAsync(ClaimsPrincipal user, Guid folderId, ResourceOp op, CancellationToken ct = default);

    /// <summary>
    /// All folder ids the principal can at least read, including inherited grants. Returned as a
    /// set so list endpoints can filter with <c>WHERE FolderId IN (...)</c>. For global-Admin the
    /// result is unrestricted: callers bypass the filter instead of emitting an empty IN clause.
    /// </summary>
    Task<AccessibleFolderSet> GetAccessibleFolderIdsAsync(ClaimsPrincipal user, CancellationToken ct = default);

    /// <summary>
    /// Computes the capability flags for a workflow in <paramref name="folderId"/>, so DTO
    /// builders can ship per-row capabilities with list and detail responses instead of
    /// requiring a second round-trip.
    /// </summary>
    Task<ResourceCapabilities> GetWorkflowCapabilitiesAsync(ClaimsPrincipal user, Guid folderId, CancellationToken ct = default);

    /// <summary>Same for a folder-typed resource.</summary>
    Task<ResourceCapabilities> GetFolderCapabilitiesAsync(ClaimsPrincipal user, Guid folderId, CancellationToken ct = default);

    /// <summary>
    /// Resolves the highest <see cref="SharedFolderRole"/> the principal holds on the folder,
    /// by direct grant or ancestor inheritance. Null when there is no grant on the chain;
    /// global-Admin returns <see cref="SharedFolderRole.FolderAdmin"/>.
    /// </summary>
    Task<SharedFolderRole?> GetEffectiveFolderRoleAsync(ClaimsPrincipal user, Guid folderId, CancellationToken ct = default);

    /// <summary>
    /// Drops every per-request cache entry. Mutating endpoints (folder create, move, delete,
    /// grant, revoke) call this after the mutation so later capability lookups in the same
    /// request reflect the new state instead of pre-mutation values.
    /// </summary>
    void InvalidateAll();
}

/// <summary>
/// Result of <see cref="IResourceAuthorizationService.GetAccessibleFolderIdsAsync"/>. When
/// <see cref="IsUnrestricted"/> is true (global-Admin), <see cref="FolderIds"/> is empty and
/// callers skip the filter instead of passing an empty IN clause, which would return no rows.
/// A user without any folder access has <see cref="IsUnrestricted"/> false and the same empty
/// <see cref="FolderIds"/>.
/// </summary>
public sealed class AccessibleFolderSet
{
    public bool IsUnrestricted { get; init; }
    public HashSet<Guid> FolderIds { get; init; } = [];

    public static readonly AccessibleFolderSet Unrestricted = new() { IsUnrestricted = true };
    public static readonly AccessibleFolderSet None = new() { IsUnrestricted = false };
}
