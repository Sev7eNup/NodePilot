using System.Security.Claims;
using FluentAssertions;
using NodePilot.Api.Security;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Security.Ldap;

/// <summary>
/// Group-aware folder permissions: a Group-grant whose SID matches one of the user's
/// normalized AD membership snapshot must contribute to the user's effective folder role
/// exactly like a per-user grant does. Group memberships intentionally do not travel in
/// browser JWTs.
/// </summary>
public sealed class GroupAwareAuthorizationTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _conn;
    private readonly NodePilotDbContext _db;

    private readonly Guid _ldapUserId = Guid.NewGuid();
    private readonly Guid _localUserId = Guid.NewGuid();

    private const string FinanceGroupSid = "S-1-5-21-1004336348-1177238915-682003330-2001";
    private const string AuditorsGroupSid = "S-1-5-21-1004336348-1177238915-682003330-2002";
    private const string UnusedGroupSid = "S-1-5-21-1004336348-1177238915-682003330-9999";

    private readonly Guid _financeId = Guid.NewGuid();
    private readonly Guid _financeReportsId = Guid.NewGuid();

    public GroupAwareAuthorizationTests()
    {
        var (conn, db) = TestDbFactory.CreateWithConnection();
        _conn = conn;
        _db = db;

        _db.Users.AddRange(
            new User
            {
                Id = _ldapUserId,
                Username = "directory-user@example.test",
                Provider = AuthProvider.Ldap,
                IsActive = true,
            },
            new User
            {
                Id = _localUserId,
                Username = "local-user",
                Provider = AuthProvider.Local,
                IsActive = true,
            });

        var rootId = SharedWorkflowFolder.RootFolderId;
        _db.SharedWorkflowFolders.AddRange(
            new SharedWorkflowFolder { Id = _financeId, ParentFolderId = rootId, Name = "Finance", Path = "/Finance", Depth = 1 },
            new SharedWorkflowFolder { Id = _financeReportsId, ParentFolderId = _financeId, Name = "Reports", Path = "/Finance/Reports", Depth = 2 });

        // /Finance carries a Group-grant for the Finance AD-Group SID. Anyone whose token
        // claims include that SID gets FolderEditor on /Finance + everything under it.
        // /Finance/Reports carries a Group-grant for the Auditors group at FolderViewer —
        // tests that "highest role wins" when a user is in both groups.
        _db.SharedFolderPermissions.AddRange(
            new SharedFolderPermission
            {
                Id = Guid.NewGuid(),
                FolderId = _financeId,
                PrincipalType = FolderPrincipalType.Group,
                PrincipalKey = FinanceGroupSid,
                Role = SharedFolderRole.FolderEditor,
            },
            new SharedFolderPermission
            {
                Id = Guid.NewGuid(),
                FolderId = _financeReportsId,
                PrincipalType = FolderPrincipalType.Group,
                PrincipalKey = AuditorsGroupSid,
                Role = SharedFolderRole.FolderViewer,
            });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private ResourceAuthorizationService NewService() => new(_db);

    private ClaimsPrincipal LdapUser(Guid userId, params string[] groupSids)
    {
        foreach (var sid in groupSids.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_db.DirectoryMemberships.Any(m => m.UserId == userId && m.GroupKey == sid))
            {
                _db.DirectoryMemberships.Add(new DirectoryMembership
                {
                    UserId = userId,
                    GroupKey = sid,
                    LastSeenAt = DateTime.UtcNow,
                });
            }
        }
        _db.SaveChanges();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Role, nameof(UserRole.Operator)),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public async Task GroupGrant_OnAncestorFolder_AppliesToDescendant()
    {
        var svc = NewService();
        var user = LdapUser(_ldapUserId, FinanceGroupSid);

        var role = await svc.GetEffectiveFolderRoleAsync(user, _financeReportsId);

        role.Should().Be(SharedFolderRole.FolderEditor);
    }

    [Fact]
    public async Task NoMatchingGroup_ReturnsNoRole()
    {
        var svc = NewService();
        var user = LdapUser(_ldapUserId, UnusedGroupSid);

        var role = await svc.GetEffectiveFolderRoleAsync(user, _financeId);

        role.Should().BeNull();
    }

    [Fact]
    public async Task UserWithMultipleGroups_HighestRoleWins()
    {
        var svc = NewService();
        // /Finance/Reports has Auditors=FolderViewer, /Finance (ancestor) has Finance=FolderEditor.
        // FolderEditor must win.
        var user = LdapUser(_ldapUserId, AuditorsGroupSid, FinanceGroupSid);

        var role = await svc.GetEffectiveFolderRoleAsync(user, _financeReportsId);

        role.Should().Be(SharedFolderRole.FolderEditor);
    }

    [Fact]
    public async Task LocalUserWithoutDirectoryMemberships_GetsNoFolderRole()
    {
        var svc = NewService();
        var localOnly = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, _localUserId.ToString()),
            new Claim(ClaimTypes.Role, nameof(UserRole.Operator)),
        }, "test"));

        var role = await svc.GetEffectiveFolderRoleAsync(localOnly, _financeId);

        role.Should().BeNull();
    }

    [Fact]
    public async Task GroupAndUserGrants_Merge()
    {
        // Add a per-user grant on /Finance/Reports at FolderAdmin alongside the existing
        // group-grant at /Finance (FolderEditor). User-grant must merge in (and Admin > Editor).
        _db.SharedFolderPermissions.Add(new SharedFolderPermission
        {
            Id = Guid.NewGuid(),
            FolderId = _financeReportsId,
            PrincipalType = FolderPrincipalType.User,
            PrincipalKey = _ldapUserId.ToString("D"),
            Role = SharedFolderRole.FolderAdmin,
        });
        await _db.SaveChangesAsync();

        var svc = NewService();
        var user = LdapUser(_ldapUserId, FinanceGroupSid);

        var role = await svc.GetEffectiveFolderRoleAsync(user, _financeReportsId);

        role.Should().Be(SharedFolderRole.FolderAdmin);
    }

    [Fact]
    public async Task AccessibleFolderIds_IncludesGroupGrantedSubtree()
    {
        var svc = NewService();
        var user = LdapUser(_ldapUserId, FinanceGroupSid);

        var accessible = await svc.GetAccessibleFolderIdsAsync(user);

        accessible.IsUnrestricted.Should().BeFalse();
        accessible.FolderIds.Should().Contain(_financeId);
        accessible.FolderIds.Should().Contain(_financeReportsId);
    }

    [Fact]
    public async Task EmptyOrWhitespaceDirectoryMembership_Ignored()
    {
        var svc = NewService();
        var user = LdapUser(_ldapUserId, "", "   ", FinanceGroupSid);

        var role = await svc.GetEffectiveFolderRoleAsync(user, _financeId);

        role.Should().Be(SharedFolderRole.FolderEditor);
    }

    [Fact]
    public async Task OidcGroupGrant_MatchesOnlyTheExactIssuerNamespace()
    {
        const string issuer = "https://login.example.test/tenant/v2.0";
        const string otherIssuer = "https://login.example.test/other/v2.0";
        const string opaqueGroup = "finance-team";
        var matchingUser = new User
        {
            Id = Guid.NewGuid(), Username = "oidc@example.test", Provider = AuthProvider.Oidc,
            Role = UserRole.Operator, IsActive = true,
        };
        var otherUser = new User
        {
            Id = Guid.NewGuid(), Username = "other@example.test", Provider = AuthProvider.Oidc,
            Role = UserRole.Operator, IsActive = true,
        };
        _db.AddRange(
            matchingUser,
            otherUser,
            new ExternalIdentity
            {
                Id = Guid.NewGuid(), UserId = matchingUser.Id,
                Authority = issuer, Subject = "matching-user",
            },
            new ExternalIdentity
            {
                Id = Guid.NewGuid(), UserId = otherUser.Id,
                Authority = otherIssuer, Subject = "other-user",
            },
            new DirectoryMembership
            {
                UserId = matchingUser.Id, Authority = issuer, GroupKey = opaqueGroup,
            },
            new DirectoryMembership
            {
                UserId = otherUser.Id, Authority = otherIssuer, GroupKey = opaqueGroup,
            },
            new SharedFolderPermission
            {
                Id = Guid.NewGuid(), FolderId = _financeId,
                PrincipalType = FolderPrincipalType.Group,
                PrincipalAuthority = issuer,
                PrincipalKey = opaqueGroup,
                Role = SharedFolderRole.FolderEditor,
            });
        await _db.SaveChangesAsync();

        static ClaimsPrincipal Principal(User user) => new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Role, nameof(UserRole.Operator)),
        }, "test"));

        (await NewService().GetEffectiveFolderRoleAsync(Principal(matchingUser), _financeId))
            .Should().Be(SharedFolderRole.FolderEditor);
        (await NewService().GetEffectiveFolderRoleAsync(Principal(otherUser), _financeId))
            .Should().BeNull();
    }
}
