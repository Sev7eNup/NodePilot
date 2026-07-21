using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.Security;
using NodePilot.Api.Security.Ldap;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using Xunit;
using NodePilot.TestCommons;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// Coverage for phase PR5 of the LDAP/Windows-SSO rollout: AuthController.WindowsLogin. We
/// bypass the Negotiate middleware and
/// drive the controller with a synthetic ClaimsPrincipal carrying the same claim shape
/// that the Negotiate handler would emit (PrimarySid + zero or more GroupSid claims),
/// so the test runs without real Kerberos infrastructure.
/// </summary>
public sealed class AuthControllerWindowsTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _conn;
    private readonly NodePilotDbContext _db;
    private readonly string _contentRoot;
    private readonly IHostEnvironment _env;

    public AuthControllerWindowsTests()
    {
        var (conn, db) = NodePilot.TestCommons.TestDbFactory.CreateWithConnection();
        _conn = conn;
        _db = db;
        _contentRoot = Path.Combine(Path.GetTempPath(), "AuthControllerWindowsTests-" + Guid.NewGuid());
        Directory.CreateDirectory(_contentRoot);
        var envMock = new Mock<IHostEnvironment>();
        envMock.SetupGet(e => e.ContentRootPath).Returns(_contentRoot);
        envMock.SetupGet(e => e.EnvironmentName).Returns("Test");
        _env = envMock.Object;

        // Rollout phase PR10 added an empty-DB bootstrap gate; bypass it here so these
        // tests exercise the post-bootstrap mainline. Bootstrap-gate behaviour is covered
        // by the dedicated PR10 regression tests.
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
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_contentRoot, recursive: true); } catch { }
    }

    private const string TestSecret = "NodePilot-Test-Secret-Key-Minimum-32-Characters!";

    private sealed class TestJwtKeyProvider : IJwtKeyProvider
    {
        public string Key => TestSecret;
    }

    private static IConfiguration NewConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = TestSecret,
            ["Jwt:Issuer"] = "NodePilot",
            ["Jwt:Audience"] = "NodePilot",
        }).Build();

    private DefaultHttpContext NewHttpContextWithIdentity(ClaimsPrincipal principal)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_env);
        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = principal,
        };
    }

    private (AuthController controller, CapturingAuditWriter audit) NewController(
        ClaimsPrincipal principal,
        LdapDirectorySnapshot? directorySnapshot = null)
    {
        var cfg = NewConfig();
        var key = new TestJwtKeyProvider();
        var audit = new CapturingAuditWriter();
        var issuer = new AuthSessionIssuer(cfg, key, audit);

        var sid = principal.FindFirstValue(ClaimTypes.PrimarySid);
        var authoritativeGroups = directorySnapshot?.GroupSids
            ?? principal.FindAll(ClaimTypes.GroupSid).Select(claim => claim.Value).ToArray();
        if (authoritativeGroups.Count == 0 && !string.IsNullOrWhiteSpace(sid))
            authoritativeGroups = ["S-1-5-21-1-1-1-9999"];
        var ldapOpts = new LdapOptions
        {
            Enabled = false,
            AllowedGroupSids = authoritativeGroups.Take(1).ToList(),
        };
        var optsMonitor = new StaticOptionsMonitor<LdapOptions>(ldapOpts);
        var mapper = new ExternalUserMapper(_db, optsMonitor, audit,
            new MemoryCache(new MemoryCacheOptions()), NullLogger<ExternalUserMapper>.Instance);
        var directory = new FakeDirectoryAdapter
        {
            Snapshot = directorySnapshot ?? (string.IsNullOrWhiteSpace(sid)
                ? null
                : new LdapDirectorySnapshot(
                    sid,
                    true,
                    principal.FindFirstValue(ClaimTypes.Name) ?? "alice@firma.de",
                    principal.FindFirstValue(ClaimTypes.Name) ?? "Alice",
                    authoritativeGroups)),
        };

        var ctx = NewHttpContextWithIdentity(principal);
        var controller = new AuthController(_db, cfg, audit, key, issuer,
            ldapAuthenticator: null,
            externalUserMapper: mapper,
            ldapOptions: optsMonitor,
            directoryAdapter: directory)
        {
            ControllerContext = new ControllerContext { HttpContext = ctx },
        };
        return (controller, audit);
    }

    private sealed class FakeDirectoryAdapter : ILdapConnectionAdapter
    {
        public LdapDirectorySnapshot? Snapshot { get; init; }

        public Task<LdapAuthResult?> AuthenticateAsync(
            string upn, string password, CancellationToken ct) => Task.FromResult<LdapAuthResult?>(null);

        public Task<LdapDirectorySnapshot?> LookupBySubjectAsync(
            string subject, CancellationToken ct) => Task.FromResult(Snapshot);
    }

    private static ClaimsPrincipal WindowsPrincipal(
        string sid = "S-1-5-21-1-1-1-1001",
        string name = @"FIRMA\alice",
        params string[] groupSids)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, name),
            new(ClaimTypes.Name, name),
            new(ClaimTypes.PrimarySid, sid),
        };
        claims.AddRange(groupSids.Select(g => new Claim(ClaimTypes.GroupSid, g)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Negotiate", ClaimTypes.Name, ClaimTypes.Role));
    }

    [Fact]
    public async Task WindowsLogin_NewUser_JitProvisionsWithProviderWindows()
    {
        var (controller, audit) = NewController(WindowsPrincipal(
            sid: "S-1-5-21-1-1-1-1001",
            name: @"FIRMA\alice",
            groupSids: "S-1-5-21-1-1-1-512"));

        var result = await controller.WindowsLogin(default);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        // Windows SSO is browser-only and ambient-credential driven, so it never returns the
        // JWT in the body — identity only. The token is delivered via the httpOnly cookie.
        var login = ok.Value.Should().BeOfType<AuthIdentityResponse>().Subject;
        login.Username.Should().Be(@"FIRMA\alice");

        var persisted = await _db.Users.SingleAsync(u => u.Provider == AuthProvider.Windows);
        persisted.Provider.Should().Be(AuthProvider.Windows);
        persisted.ExternalId.Should().Be("S-1-5-21-1-1-1-1001");
        persisted.PasswordHash.Should().BeNull();

        audit.Calls.Should().Contain(c => c.Action == "USER_WINDOWS_JIT_CREATED");
    }

    [Fact]
    public async Task WindowsLogin_LastAdminWouldBeDemoted_RefusesLogin()
    {
        const string externalId = "S-1-5-21-1-1-1-1001";
        _db.Users.RemoveRange(_db.Users);
        _db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = @"FIRMA\alice",
            Provider = AuthProvider.Windows,
            ExternalId = externalId,
            KnownGroupSidsJson = "[\"S-1-5-21-1-1-1-512\"]",
            Role = UserRole.Admin,
            IsActive = true,
            SecurityStamp = 6,
        });
        await _db.SaveChangesAsync();

        var (controller, audit) = NewController(WindowsPrincipal(
            sid: externalId,
            name: @"FIRMA\alice"));

        var result = await controller.WindowsLogin(default);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
        controller.Response.Headers.SetCookie.ToString().Should().NotContain("np_auth=");
        (await _db.AuditLog.CountAsync(c => c.Action == "USER_WINDOWS_REFUSED_LAST_ADMIN"))
            .Should().Be(1);
    }

    [Fact]
    public async Task WindowsLogin_GroupsStayServerSideAndOutOfToken()
    {
        var (controller, _) = NewController(WindowsPrincipal(
            groupSids: new[] { "S-1-5-21-1-1-1-512", "S-1-5-21-1-1-1-513" }));

        var result = await controller.WindowsLogin(default);

        // The JWT is NOT in the body for Windows SSO — extract it from the np_auth cookie to
        // verify the group SIDs are still stamped onto the issued token.
        result.Result.Should().BeOfType<OkObjectResult>();
        var setCookie = controller.Response.Headers.SetCookie.ToString();
        var match = System.Text.RegularExpressions.Regex.Match(setCookie, @"np_auth=([^;]+)");
        match.Success.Should().BeTrue("the issued JWT must be set as the np_auth cookie");
        var token = new JwtSecurityTokenHandler().ReadJwtToken(Uri.UnescapeDataString(match.Groups[1].Value));
        token.Claims.Should().NotContain(c => c.Type == ClaimTypes.GroupSid);
        var userId = Guid.Parse(token.Claims.Single(c => c.Type == ClaimTypes.NameIdentifier).Value);
        (await _db.DirectoryMemberships
                .Where(m => m.UserId == userId)
                .Select(m => m.GroupKey)
                .ToListAsync())
            .Should().BeEquivalentTo("S-1-5-21-1-1-1-512", "S-1-5-21-1-1-1-513");
    }

    [Fact]
    public async Task WindowsLogin_UsesFreshLdapsGroupsInsteadOfKerberosPacGroups()
    {
        const string sid = "S-1-5-21-1-1-1-1001";
        const string stalePacGroup = "S-1-5-21-1-1-1-512";
        const string currentDirectoryGroup = "S-1-5-21-1-1-1-700";
        var principal = WindowsPrincipal(sid, @"FIRMA\alice", stalePacGroup);
        var (controller, _) = NewController(principal, new LdapDirectorySnapshot(
            sid, true, "alice@firma.de", "Alice", [currentDirectoryGroup]));

        var result = await controller.WindowsLogin(default);

        result.Result.Should().BeOfType<OkObjectResult>();
        var user = await _db.Users.SingleAsync(u => u.Provider == AuthProvider.Windows);
        (await _db.DirectoryMemberships.Where(x => x.UserId == user.Id)
                .Select(x => x.GroupKey).ToListAsync())
            .Should().Equal(currentDirectoryGroup);
    }

    [Fact]
    public async Task WindowsLogin_NoPrimarySid_Returns401WithAudit()
    {
        // A misconfigured Negotiate handler that yields no PrimarySid is treated as a
        // hard failure; the user can't be JIT-provisioned without a stable SID.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, @"FIRMA\alice"),
        }, "Negotiate"));
        var (controller, audit) = NewController(principal);

        var result = await controller.WindowsLogin(default);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
        audit.Calls.Should().Contain(c => c.Action == "LOGIN_FAILED" && c.Details!.Contains("windows_no_primary_sid"));
    }

    [Fact]
    public async Task WindowsLogin_DeactivatedUser_Returns401()
    {
        _db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = @"FIRMA\alice",
            Provider = AuthProvider.Windows,
            ExternalId = "S-1-5-21-1-1-1-1001",
            Role = UserRole.Viewer,
            IsActive = false,
            PasswordChangedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var (controller, _) = NewController(WindowsPrincipal());
        var result = await controller.WindowsLogin(default);

        var unauth = result.Result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        unauth.Value!.ToString()!.Should().Contain("disabled");
    }

    [Fact]
    public async Task WindowsLogin_SecondLoginUpdatesGroupSids()
    {
        // First login creates the row; second login (with new groups) updates it.
        var (controller1, _) = NewController(WindowsPrincipal(
            groupSids: "S-1-5-21-1-1-1-512"));
        await controller1.WindowsLogin(default);

        // Detach first context's tracker by re-creating mapper context dependency.
        _db.ChangeTracker.Clear();

        var (controller2, audit2) = NewController(WindowsPrincipal(
            groupSids: new[] { "S-1-5-21-1-1-1-512", "S-1-5-21-1-1-1-700" }));
        await controller2.WindowsLogin(default);

        var persisted = await _db.Users.SingleAsync(u => u.Provider == AuthProvider.Windows);
        persisted.KnownGroupSidsJson.Should().Contain("S-1-5-21-1-1-1-700");
        (await _db.AuditLog.CountAsync(c => c.Action == "USER_WINDOWS_JIT_UPDATED"))
            .Should().Be(1);
    }
}
