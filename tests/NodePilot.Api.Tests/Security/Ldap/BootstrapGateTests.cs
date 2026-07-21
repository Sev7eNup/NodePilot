using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodePilot.Api.Security.Ldap;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Security.Ldap;

/// <summary>
/// External-auth JIT must not provision any row before a local break-glass administrator
/// exists, because doing so closes the <c>X-Setup-Token</c> bootstrap window and could
/// leave the instance without an independent recovery path.
/// </summary>
public sealed class BootstrapGateTests : IDisposable
{
    private const string AllowedGroup = "S-1-5-21-1-1-1-9999";
    private readonly Microsoft.Data.Sqlite.SqliteConnection _conn;
    private readonly NodePilotDbContext _db;
    private readonly CapturingAuditWriter _audit = new();

    public BootstrapGateTests()
    {
        var (conn, db) = TestDbFactory.CreateWithConnection();
        _conn = conn;
        _db = db;
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private ExternalUserMapper NewMapper(LdapOptions options) =>
        new(_db,
            new StaticOptionsMonitor<LdapOptions>(options),
            _audit,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ExternalUserMapper>.Instance);

    private static LdapOptions OptionsWithAdminMapping(string adminGroupSid) => new()
    {
        Enabled = true,
        UpnSuffix = "firma.de",
        AllowedGroupSids = [adminGroupSid],
        GlobalRoleMappings = new List<GlobalRoleMapping>
        {
            new() { GroupSid = adminGroupSid, Role = UserRole.Admin },
        },
    };

    [Fact]
    public async Task EmptyDb_NonAdminLdap_Refused_NoUserCreated()
    {
        // No GlobalRoleMappings → user resolves to Viewer. With an empty DB this attempt
        // would close the bootstrap window if we let it through. Expect refusal.
        var mapper = NewMapper(new LdapOptions
        {
            Enabled = true, UpnSuffix = "firma.de", AllowedGroupSids = [AllowedGroup],
        });
        var ldap = new LdapAuthResult("guid-1", "viewer@firma.de", "Viewer", [AllowedGroup]);

        var outcome = await mapper.MapAsync(ldap, default);

        outcome.Result.Should().Be(ExternalUserMapResult.RefusedBootstrapNotAdmin);
        outcome.User.Should().BeNull();
        (await _db.Users.CountAsync()).Should().Be(0);
        _audit.Calls.Should().ContainSingle(c => c.Action == "USER_LDAP_REFUSED_BOOTSTRAP");
    }

    [Fact]
    public async Task EmptyDb_NonAdminWindows_Refused_NoUserCreated()
    {
        // Symmetric for the Windows path.
        var mapper = NewMapper(new LdapOptions
        {
            Enabled = true, UpnSuffix = "firma.de", AllowedGroupSids = [AllowedGroup],
        });
        var ldap = new LdapAuthResult("S-1-5-21-1-1-1-1001", @"FIRMA\alice", "Alice", [AllowedGroup]);

        var outcome = await mapper.MapAsync(ldap, AuthProvider.Windows, default);

        outcome.Result.Should().Be(ExternalUserMapResult.RefusedBootstrapNotAdmin);
        outcome.User.Should().BeNull();
        (await _db.Users.CountAsync()).Should().Be(0);
        _audit.Calls.Should().ContainSingle(c => c.Action == "USER_WINDOWS_REFUSED_BOOTSTRAP");
    }

    [Fact]
    public async Task EmptyDb_AdminMappedLdap_Refused_UntilLocalBreakGlassBootstrap()
    {
        // External authentication never bootstraps a fresh instance, even when its
        // directory groups resolve to Admin. A local break-glass account must exist first.
        const string adminSid = "S-1-5-21-1-1-1-512";
        var mapper = NewMapper(OptionsWithAdminMapping(adminSid));
        var ldap = new LdapAuthResult("guid-admin", "admin@firma.de", "Admin",
            new[] { adminSid });

        var outcome = await mapper.MapAsync(ldap, default);

        outcome.Result.Should().Be(ExternalUserMapResult.RefusedBootstrapNotAdmin);
        outcome.User.Should().BeNull();
        (await _db.Users.CountAsync()).Should().Be(0);
        _audit.Calls.Should().ContainSingle(c => c.Action == "USER_LDAP_REFUSED_BOOTSTRAP");
    }

    [Fact]
    public async Task NonEmptyDb_NonAdminLdap_Allowed_AsViewer()
    {
        // Once an admin exists, the gate stops applying — non-admin JIT proceeds normally.
        _db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = "preexisting-admin",
            PasswordHash = "$2a$12$dummy",
            Provider = AuthProvider.Local,
            Role = UserRole.Admin,
            IsActive = true,
            IsBreakGlass = true,
            PasswordChangedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var mapper = NewMapper(new LdapOptions
        {
            Enabled = true, UpnSuffix = "firma.de", AllowedGroupSids = [AllowedGroup],
        });
        var ldap = new LdapAuthResult("guid-2", "viewer@firma.de", "Viewer", [AllowedGroup]);

        var outcome = await mapper.MapAsync(ldap, default);

        outcome.Result.Should().Be(ExternalUserMapResult.Mapped);
        outcome.User!.Role.Should().Be(UserRole.Viewer);
        (await _db.Users.CountAsync()).Should().Be(2);
    }
}
