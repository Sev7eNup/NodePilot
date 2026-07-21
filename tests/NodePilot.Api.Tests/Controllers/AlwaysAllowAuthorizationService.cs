using System.Security.Claims;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// Permissive <see cref="IResourceAuthorizationService"/> for the controller-test harness.
/// Returns "yes" to every check and an Unrestricted accessible-folder set. Tests that
/// specifically exercise RBAC denial paths use the dedicated RBAC fixture; this stub is
/// only for the existing controller test surface that pre-dates RBAC and would otherwise
/// have to backfill folder permissions for every test principal.
/// </summary>
internal sealed class AlwaysAllowAuthorizationService : IResourceAuthorizationService
{
    public Task<bool> CanAccessWorkflowAsync(ClaimsPrincipal user, Guid folderId, ResourceOp op, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<bool> CanAccessFolderAsync(ClaimsPrincipal user, Guid folderId, ResourceOp op, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<AccessibleFolderSet> GetAccessibleFolderIdsAsync(ClaimsPrincipal user, CancellationToken ct = default)
        => Task.FromResult(AccessibleFolderSet.Unrestricted);

    public Task<ResourceCapabilities> GetWorkflowCapabilitiesAsync(ClaimsPrincipal user, Guid folderId, CancellationToken ct = default)
        => Task.FromResult(ResourceCapabilities.All);

    public Task<ResourceCapabilities> GetFolderCapabilitiesAsync(ClaimsPrincipal user, Guid folderId, CancellationToken ct = default)
        => Task.FromResult(ResourceCapabilities.All);

    public Task<SharedFolderRole?> GetEffectiveFolderRoleAsync(ClaimsPrincipal user, Guid folderId, CancellationToken ct = default)
        => Task.FromResult<SharedFolderRole?>(SharedFolderRole.FolderAdmin);

    // No-op: stub has no caches to drop.
    public void InvalidateAll() { }
}
