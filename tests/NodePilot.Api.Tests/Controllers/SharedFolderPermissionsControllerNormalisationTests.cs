using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.Security;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// PR9 Fix 4 regression: PrincipalKey written by the grant endpoint MUST be in the same
/// canonical form ResourceAuthorizationService compares against, otherwise on a
/// case-sensitive provider (Postgres default) the grant silently has no effect.
/// </summary>
public sealed class SharedFolderPermissionsControllerNormalisationTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _conn;
    private readonly NodePilotDbContext _db;

    public SharedFolderPermissionsControllerNormalisationTests()
    {
        var (conn, db) = NodePilot.TestCommons.TestDbFactory.CreateWithConnection();
        _conn = conn;
        _db = db;
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private SharedFolderPermissionsController NewController(Guid adminId)
    {
        var authz = new ResourceAuthorizationService(_db);
        var controller = new SharedFolderPermissionsController(_db, NoopAuditWriter.Instance, authz);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, adminId.ToString()),
                    new Claim(ClaimTypes.Role, nameof(UserRole.Admin)),
                }, "test")),
            },
        };
        return controller;
    }

    [Fact]
    public async Task GrantUser_PrincipalKeyMixedCase_StoredAsLowercaseCanonical()
    {
        var adminId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        _db.Users.Add(new User
        {
            Id = targetId,
            Username = "alice",
            PasswordHash = "x",
            Provider = AuthProvider.Local,
            Role = UserRole.Operator,
        });
        await _db.SaveChangesAsync();

        var folderId = SharedWorkflowFolder.RootFolderId;
        var controller = NewController(adminId);
        var req = new GrantSharedFolderPermissionRequest(
            FolderPrincipalType.User,
            // Mixed-case Guid string (Guid.TryParse accepts it; ToString("D") would normalise).
            targetId.ToString("D").ToUpperInvariant(),
            SharedFolderRole.FolderEditor);

        var result = await controller.Grant(folderId, req, default);

        result.Result.Should().BeOfType<OkObjectResult>();
        var stored = await _db.SharedFolderPermissions.SingleAsync();
        stored.PrincipalKey.Should().Be(targetId.ToString("D"));   // canonical lowercase
        stored.PrincipalKey.Should().NotBe(req.PrincipalKey);      // ie. NOT the raw uppercase
    }

    [Fact]
    public async Task GrantUser_NormalisedKey_AuthorizationActuallyWorks()
    {
        // The whole point of normalisation: a grant created via mixed-case API input must
        // make the user's authorization decisions return true. Without normalisation this
        // fails on case-sensitive collations.
        var adminId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        _db.Users.Add(new User
        {
            Id = targetId,
            Username = "alice",
            PasswordHash = "x",
            Provider = AuthProvider.Local,
            Role = UserRole.Operator,
            IsActive = true,
        });
        await _db.SaveChangesAsync();

        var folderId = SharedWorkflowFolder.RootFolderId;
        var controller = NewController(adminId);
        await controller.Grant(folderId, new GrantSharedFolderPermissionRequest(
            FolderPrincipalType.User,
            targetId.ToString("D").ToUpperInvariant(),
            SharedFolderRole.FolderEditor), default);

        var authz = new ResourceAuthorizationService(_db);
        var asTarget = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, targetId.ToString()),
            new Claim(ClaimTypes.Role, nameof(UserRole.Operator)),
        }, "test"));

        var role = await authz.GetEffectiveFolderRoleAsync(asTarget, folderId);

        role.Should().Be(SharedFolderRole.FolderEditor);
    }

    [Fact]
    public async Task GrantGroup_LowercaseSid_StoredAsUppercaseCanonical()
    {
        var adminId = Guid.NewGuid();
        var folderId = SharedWorkflowFolder.RootFolderId;
        var controller = NewController(adminId);
        var lowercaseSid = "s-1-5-21-1004336348-1177238915-682003330-512";
        var req = new GrantSharedFolderPermissionRequest(
            FolderPrincipalType.Group,
            lowercaseSid,
            SharedFolderRole.FolderViewer);

        var result = await controller.Grant(folderId, req, default);

        result.Result.Should().BeOfType<OkObjectResult>();
        var stored = await _db.SharedFolderPermissions.SingleAsync();
        stored.PrincipalKey.Should().Be(lowercaseSid.ToUpperInvariant());
        stored.PrincipalAuthority.Should().Be(ExternalIdentity.ActiveDirectoryAuthority);
    }

    [Fact]
    public async Task GrantOidcGroup_PreservesIssuerAndOpaqueGroupKey()
    {
        const string issuer = "https://login.example.test/tenant/v2.0";
        const string groupKey = "finance-team";
        var controller = NewController(Guid.NewGuid());
        var req = new GrantSharedFolderPermissionRequest(
            FolderPrincipalType.Group,
            groupKey,
            SharedFolderRole.FolderViewer)
        {
            PrincipalAuthority = issuer,
        };

        var result = await controller.Grant(SharedWorkflowFolder.RootFolderId, req, default);

        result.Result.Should().BeOfType<OkObjectResult>();
        var stored = await _db.SharedFolderPermissions.SingleAsync();
        stored.PrincipalAuthority.Should().Be(issuer);
        stored.PrincipalKey.Should().Be(groupKey);
    }

    [Fact]
    public async Task RevokeUserGrant_BumpsAffectedUsersSecurityStamp()
    {
        var target = new User
        {
            Id = Guid.NewGuid(), Username = "alice", PasswordHash = "x",
            Provider = AuthProvider.Local, Role = UserRole.Operator,
            IsActive = true, SecurityStamp = 3,
        };
        var permission = new SharedFolderPermission
        {
            Id = Guid.NewGuid(), FolderId = SharedWorkflowFolder.RootFolderId,
            PrincipalType = FolderPrincipalType.User,
            PrincipalKey = target.Id.ToString("D"),
            Role = SharedFolderRole.FolderEditor,
        };
        _db.AddRange(target, permission);
        await _db.SaveChangesAsync();

        await NewController(Guid.NewGuid()).Revoke(
            permission.FolderId, permission.Id, CancellationToken.None);

        (await _db.Users.AsNoTracking().SingleAsync(u => u.Id == target.Id))
            .SecurityStamp.Should().Be(4);
    }

    [Fact]
    public async Task DowngradeGroupGrant_BumpsMatchingExternalUsersOnly()
    {
        const string sid = "S-1-5-21-1004336348-1177238915-682003330-512";
        var member = new User
        {
            Id = Guid.NewGuid(), Username = "member@example.test", PasswordHash = "x",
            Provider = AuthProvider.Ldap, Role = UserRole.Operator, IsActive = true,
            KnownGroupSidsJson = $"[\"{sid.ToLowerInvariant()}\"]", SecurityStamp = 5,
        };
        var outsider = new User
        {
            Id = Guid.NewGuid(), Username = "outsider@example.test", PasswordHash = "x",
            Provider = AuthProvider.Ldap, Role = UserRole.Operator, IsActive = true,
            KnownGroupSidsJson = "[]", SecurityStamp = 9,
        };
        var permission = new SharedFolderPermission
        {
            Id = Guid.NewGuid(), FolderId = SharedWorkflowFolder.RootFolderId,
            PrincipalType = FolderPrincipalType.Group, PrincipalKey = sid,
            Role = SharedFolderRole.FolderEditor,
        };
        var membership = new DirectoryMembership
        {
            UserId = member.Id,
            Authority = ExternalIdentity.ActiveDirectoryAuthority,
            GroupKey = sid,
        };
        _db.AddRange(member, outsider, permission, membership);
        await _db.SaveChangesAsync();

        await NewController(Guid.NewGuid()).Update(
            permission.FolderId, permission.Id,
            new UpdateSharedFolderPermissionRequest(SharedFolderRole.FolderViewer),
            CancellationToken.None);

        var users = await _db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id);
        users[member.Id].SecurityStamp.Should().Be(6);
        users[outsider.Id].SecurityStamp.Should().Be(9);
    }
}
