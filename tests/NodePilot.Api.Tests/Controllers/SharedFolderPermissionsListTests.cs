using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// The grant-listing endpoint of <see cref="SharedFolderPermissionsController"/>. Its two-stage
/// gate is the interesting part: Read access alone is not enough to enumerate who else has
/// access — that would leak the principal list to every folder viewer — so a Read-but-not-Admin
/// caller must get 403 while a caller without Read at all gets 404 (existence stays hidden).
/// </summary>
public sealed class SharedFolderPermissionsListTests : IDisposable
{
    private readonly NodePilotDbContext _db = TestDbFactory.Create();
    private readonly Guid _folderId = Guid.NewGuid();

    public SharedFolderPermissionsListTests()
    {
        _db.SharedWorkflowFolders.Add(new SharedWorkflowFolder
        {
            Id = _folderId,
            ParentFolderId = SharedWorkflowFolder.RootFolderId,
            Name = "team",
            Path = "/team",
            Depth = 1,
        });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GetAll_WithoutReadAccess_Returns404SoTheFolderStaysHidden()
    {
        var result = await Controller(new GateAuthz(read: false, admin: false))
            .GetAll(_folderId, TestContext.Current.CancellationToken);

        result.Result.Should().BeOfType<NotFoundResult>(
            "a caller who cannot see the folder must not learn that it exists");
    }

    [Fact]
    public async Task GetAll_WithReadButNotAdmin_Returns403()
    {
        var result = await Controller(new GateAuthz(read: true, admin: false))
            .GetAll(_folderId, TestContext.Current.CancellationToken);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status403Forbidden,
            "enumerating grants would leak which other principals have access");
    }

    [Fact]
    public async Task GetAll_WithFolderAdmin_ReturnsTheGrants()
    {
        SeedGrant(FolderPrincipalType.Group, "S-1-5-21-1000", SharedFolderRole.FolderViewer);

        var result = await Controller().GetAll(_folderId, TestContext.Current.CancellationToken);

        Grants(result).Should().ContainSingle()
            .Which.PrincipalKey.Should().Be("S-1-5-21-1000");
    }

    [Fact]
    public async Task GetAll_UserGrant_ResolvesTheDisplayName()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice@example.test",
            Provider = AuthProvider.Local,
            PasswordHash = "hash",
            Role = UserRole.Operator,
            IsActive = true,
        };
        _db.Users.Add(user);
        _db.SaveChanges();
        SeedGrant(FolderPrincipalType.User, user.Id.ToString("D"), SharedFolderRole.FolderEditor);

        var result = await Controller().GetAll(_folderId, TestContext.Current.CancellationToken);

        Grants(result).Single().PrincipalDisplayName.Should().Be("alice@example.test");
    }

    [Fact]
    public async Task GetAll_UserGrantForADeletedUser_LeavesTheDisplayNameEmpty()
    {
        // The grant outlives the user row; the list must still render instead of throwing.
        SeedGrant(FolderPrincipalType.User, Guid.NewGuid().ToString("D"), SharedFolderRole.FolderEditor);

        var result = await Controller().GetAll(_folderId, TestContext.Current.CancellationToken);

        Grants(result).Single().PrincipalDisplayName.Should().BeNull();
    }

    [Fact]
    public async Task GetAll_MalformedUserPrincipalKey_IsToleratedWithoutNameLookup()
    {
        SeedGrant(FolderPrincipalType.User, "not-a-guid", SharedFolderRole.FolderViewer);

        var result = await Controller().GetAll(_folderId, TestContext.Current.CancellationToken);

        Grants(result).Single().PrincipalDisplayName.Should().BeNull();
    }

    [Fact]
    public async Task GetAll_GroupGrant_CarriesTheAuthorityAndNoDisplayName()
    {
        SeedGrant(FolderPrincipalType.Group, "S-1-5-21-2000", SharedFolderRole.FolderOperator);

        var result = await Controller().GetAll(_folderId, TestContext.Current.CancellationToken);

        var grant = Grants(result).Single();
        grant.PrincipalDisplayName.Should().BeNull("group-name resolution is not part of V1");
        grant.PrincipalType.Should().Be(FolderPrincipalType.Group);
    }

    [Fact]
    public async Task GetAll_OnlyReturnsGrantsOfTheRequestedFolder()
    {
        SeedGrant(FolderPrincipalType.Group, "S-1-5-21-1000", SharedFolderRole.FolderViewer);
        // A grant on a *different* folder — FolderId is a real FK, so the sibling has to exist.
        var otherFolderId = Guid.NewGuid();
        _db.SharedWorkflowFolders.Add(new SharedWorkflowFolder
        {
            Id = otherFolderId,
            ParentFolderId = SharedWorkflowFolder.RootFolderId,
            Name = "other",
            Path = "/other",
            Depth = 1,
        });
        _db.SaveChanges();
        SeedGrant(FolderPrincipalType.Group, "S-1-5-21-9999", SharedFolderRole.FolderViewer,
            folderId: otherFolderId);

        var result = await Controller().GetAll(_folderId, TestContext.Current.CancellationToken);

        Grants(result).Should().ContainSingle().Which.PrincipalKey.Should().Be("S-1-5-21-1000");
    }

    [Fact]
    public async Task GetAll_FolderWithoutGrants_ReturnsAnEmptyList()
    {
        var result = await Controller().GetAll(_folderId, TestContext.Current.CancellationToken);

        Grants(result).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- helpers

    private void SeedGrant(
        FolderPrincipalType type, string key, SharedFolderRole role, Guid? folderId = null)
    {
        _db.SharedFolderPermissions.Add(new SharedFolderPermission
        {
            Id = Guid.NewGuid(),
            FolderId = folderId ?? _folderId,
            PrincipalType = type,
            PrincipalKey = key,
            Role = role,
            GrantedByUserId = Guid.NewGuid(),
            GrantedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    private static List<SharedFolderPermissionResponse> Grants(
        ActionResult<List<SharedFolderPermissionResponse>> result) =>
        result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<List<SharedFolderPermissionResponse>>().Subject;

    private SharedFolderPermissionsController Controller(IResourceAuthorizationService? authz = null) =>
        new(_db, NoopAuditWriter.Instance, authz ?? new AlwaysAllowAuthorizationService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [
                            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                            new Claim(ClaimTypes.Name, "admin"),
                            new Claim(ClaimTypes.Role, "Admin"),
                        ],
                        "test")),
                },
            },
        };

    /// <summary>Answers Read and Admin independently so the two-stage gate can be probed.</summary>
    private sealed class GateAuthz(bool read, bool admin) : IResourceAuthorizationService
    {
        public Task<bool> CanAccessWorkflowAsync(
            ClaimsPrincipal user, Guid folderId, ResourceOp op, CancellationToken ct = default)
            => Task.FromResult(Allowed(op));

        public Task<bool> CanAccessFolderAsync(
            ClaimsPrincipal user, Guid folderId, ResourceOp op, CancellationToken ct = default)
            => Task.FromResult(Allowed(op));

        private bool Allowed(ResourceOp op) => op == ResourceOp.Admin ? admin : read;

        public Task<AccessibleFolderSet> GetAccessibleFolderIdsAsync(
            ClaimsPrincipal user, CancellationToken ct = default)
            => Task.FromResult(AccessibleFolderSet.Unrestricted);

        public Task<ResourceCapabilities> GetWorkflowCapabilitiesAsync(
            ClaimsPrincipal user, Guid folderId, CancellationToken ct = default)
            => Task.FromResult(ResourceCapabilities.All);

        public Task<ResourceCapabilities> GetFolderCapabilitiesAsync(
            ClaimsPrincipal user, Guid folderId, CancellationToken ct = default)
            => Task.FromResult(ResourceCapabilities.All);

        public Task<SharedFolderRole?> GetEffectiveFolderRoleAsync(
            ClaimsPrincipal user, Guid folderId, CancellationToken ct = default)
            => Task.FromResult<SharedFolderRole?>(admin ? SharedFolderRole.FolderAdmin : SharedFolderRole.FolderViewer);

        public void InvalidateAll() { }
    }
}
