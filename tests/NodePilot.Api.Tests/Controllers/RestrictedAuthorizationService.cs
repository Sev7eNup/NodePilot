using System.Security.Claims;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// Restrictive <see cref="IResourceAuthorizationService"/> for tests that exercise the
/// "user has no (or limited) folder access" code paths. Pair with the desired
/// <see cref="AccessibleFolderSet"/> in the ctor — <c>None</c> for a zero-folder caller,
/// or a custom restricted set for partial access.
/// </summary>
internal sealed class RestrictedAuthorizationService : IResourceAuthorizationService
{
    private readonly AccessibleFolderSet _set;

    public RestrictedAuthorizationService(AccessibleFolderSet set)
    {
        _set = set;
    }

    public Task<bool> CanAccessWorkflowAsync(ClaimsPrincipal user, Guid folderId, ResourceOp op, CancellationToken ct = default)
        => Task.FromResult(_set.IsUnrestricted || _set.FolderIds.Contains(folderId));

    public Task<bool> CanAccessFolderAsync(ClaimsPrincipal user, Guid folderId, ResourceOp op, CancellationToken ct = default)
        => Task.FromResult(_set.IsUnrestricted || _set.FolderIds.Contains(folderId));

    public Task<AccessibleFolderSet> GetAccessibleFolderIdsAsync(ClaimsPrincipal user, CancellationToken ct = default)
        => Task.FromResult(_set);

    public Task<ResourceCapabilities> GetWorkflowCapabilitiesAsync(ClaimsPrincipal user, Guid folderId, CancellationToken ct = default)
        => Task.FromResult(ResourceCapabilities.None);

    public Task<ResourceCapabilities> GetFolderCapabilitiesAsync(ClaimsPrincipal user, Guid folderId, CancellationToken ct = default)
        => Task.FromResult(ResourceCapabilities.None);

    public Task<SharedFolderRole?> GetEffectiveFolderRoleAsync(ClaimsPrincipal user, Guid folderId, CancellationToken ct = default)
        => Task.FromResult<SharedFolderRole?>(null);

    public void InvalidateAll() { }
}
