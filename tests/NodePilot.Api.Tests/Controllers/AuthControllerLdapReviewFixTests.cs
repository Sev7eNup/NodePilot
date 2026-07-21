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
/// Regression tests for the four review findings against the LDAP / Windows-SSO feature:
/// <list type="number">
/// <item>POST /auth/refresh must carry ClaimTypes.GroupSid claims for non-Local users.</item>
/// <item>WindowsLogin must reject NTLM when AllowNtlmFallback=false.</item>
/// <item>TryLdapLoginAsync must NOT skip LDAP when an AutoLink-eligible local row exists.</item>
/// <item>Normalisation of User PrincipalKey on grant — covered separately in
///    SharedFolderPermissionsControllerNormalisationTests.</item>
/// </list>
/// </summary>
public sealed class AuthControllerLdapReviewFixTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _conn;
    private readonly NodePilotDbContext _db;
    private readonly string _contentRoot;
    private readonly IHostEnvironment _env;

    public AuthControllerLdapReviewFixTests()
    {
        var (conn, db) = NodePilot.TestCommons.TestDbFactory.CreateWithConnection();
        _conn = conn;
        _db = db;
        _contentRoot = Path.Combine(Path.GetTempPath(), "AuthControllerLdapReviewFixTests-" + Guid.NewGuid());
        Directory.CreateDirectory(_contentRoot);
        var envMock = new Mock<IHostEnvironment>();
        envMock.SetupGet(e => e.ContentRootPath).Returns(_contentRoot);
        envMock.SetupGet(e => e.EnvironmentName).Returns("Test");
        _env = envMock.Object;

        // Rollout phase PR10 added an empty-DB bootstrap gate; bypass it here so the
        // post-bootstrap mainline runs. Bootstrap behaviour itself is covered by the
        // dedicated PR10 regression tests below.
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

    private sealed class FakeAdapter : ILdapConnectionAdapter
    {
        public LdapAuthResult? Result { get; set; }
        public LdapDirectorySnapshot? Snapshot { get; set; }
        public int AuthenticateCalls { get; private set; }
        public Task<LdapAuthResult?> AuthenticateAsync(string upn, string password, CancellationToken ct)
        {
            AuthenticateCalls++;
            return Task.FromResult(Result);
        }
        public Task<LdapDirectorySnapshot?> LookupBySubjectAsync(string subject, CancellationToken ct)
            => Task.FromResult(Snapshot);
    }

    private static IConfiguration NewConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = TestSecret,
            ["Jwt:Issuer"] = "NodePilot",
            ["Jwt:Audience"] = "NodePilot",
        }).Build();

    private DefaultHttpContext NewHttpContext(ClaimsPrincipal? principal = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_env);
        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = principal ?? new ClaimsPrincipal(new ClaimsIdentity()),
        };
    }

    // --- Fix 1: refresh path carries GroupSid ---------------------------------------

    [Fact]
    public async Task Refresh_LdapUser_KeepsGroupsServerSide()
    {
        // A pre-existing LDAP user with cached groups — the refresh path must mint a token
        // that still carries those groups so folder-permissions don't silently break.
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice@firma.de",
            Provider = AuthProvider.Ldap,
            ExternalId = "guid-aaa",
            KnownGroupSidsJson = "[\"S-1-5-21-1-1-1-512\",\"S-1-5-21-1-1-1-700\"]",
            Role = UserRole.Operator,
            IsActive = true,
            PasswordChangedAt = DateTime.UtcNow,
        };
        _db.Users.Add(user);
        _db.DirectoryMemberships.AddRange(
            new DirectoryMembership { UserId = user.Id, GroupKey = "S-1-5-21-1-1-1-512" },
            new DirectoryMembership { UserId = user.Id, GroupKey = "S-1-5-21-1-1-1-700" });
        await _db.SaveChangesAsync();

        var cfg = NewConfig();
        var key = new TestJwtKeyProvider();
        var issuer = new AuthSessionIssuer(cfg, key, NoopAuditWriter.Instance);
        var controller = new AuthController(_db, cfg, NoopAuditWriter.Instance, key, issuer);

        // Build a ClaimsPrincipal as if this caller already has a valid token; the refresh
        // endpoint reads NameIdentifier from User to find the row.
        var refreshClaims = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, "old-jti-123"),
        }, "Bearer"));
        controller.ControllerContext = new ControllerContext { HttpContext = NewHttpContext(refreshClaims) };
        // Bearer-header caller → the rotated token is returned in the body (CLI/API contract).
        controller.Request.Headers.Authorization = "Bearer presented.token";

        var result = await controller.Refresh(default);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var login = ok.Value.Should().BeAssignableTo<LoginResponse>().Subject;
        var token = new JwtSecurityTokenHandler().ReadJwtToken(login.Token);
        token.Claims.Should().NotContain(c => c.Type == ClaimTypes.GroupSid);
        (await _db.DirectoryMemberships
                .Where(m => m.UserId == user.Id)
                .Select(m => m.GroupKey)
                .ToListAsync())
            .Should().BeEquivalentTo("S-1-5-21-1-1-1-512", "S-1-5-21-1-1-1-700");
    }

    [Fact]
    public async Task Refresh_LocalUser_StampsNoGroupSidClaims()
    {
        // Sanity: local users have no group claims. The fix must not regress local-user
        // refresh by accidentally adding empty/garbage GroupSid claims.
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "admin",
            Provider = AuthProvider.Local,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password1", AuthController.BCryptWorkFactor),
            Role = UserRole.Admin,
            IsActive = true,
            PasswordChangedAt = DateTime.UtcNow,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var cfg = NewConfig();
        var key = new TestJwtKeyProvider();
        var issuer = new AuthSessionIssuer(cfg, key, NoopAuditWriter.Instance);
        var controller = new AuthController(_db, cfg, NoopAuditWriter.Instance, key, issuer);

        var refreshClaims = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, "old-jti"),
        }, "Bearer"));
        controller.ControllerContext = new ControllerContext { HttpContext = NewHttpContext(refreshClaims) };
        controller.Request.Headers.Authorization = "Bearer presented.token";

        var result = await controller.Refresh(default);

        var login = ((OkObjectResult)result.Result!).Value as LoginResponse;
        var token = new JwtSecurityTokenHandler().ReadJwtToken(login!.Token);
        token.Claims.Should().NotContain(c => c.Type == ClaimTypes.GroupSid);
    }

    // --- Fix 2: NTLM enforcement when AllowNtlmFallback=false ----------------------

    [Fact]
    public async Task WindowsLogin_NtlmIdentity_AllowNtlmFalse_Rejected()
    {
        var cfg = NewConfig();
        var key = new TestJwtKeyProvider();
        var audit = new CapturingAuditWriter();
        var issuer = new AuthSessionIssuer(cfg, key, audit);
        var ldapOpts = new LdapOptions
        {
            Enabled = false,
            AllowedGroupSids = ["S-1-5-21-1-1-1-9999"],
        };
        var winOpts = new WindowsAuthOptions { Enabled = true, AllowNtlmFallback = false, NtlmDisabledByPolicy = true };
        var ldapMonitor = new StaticOptionsMonitor<LdapOptions>(ldapOpts);
        var winMonitor = new StaticOptionsMonitor<WindowsAuthOptions>(winOpts);
        var mapper = new ExternalUserMapper(_db, ldapMonitor, audit,
            new MemoryCache(new MemoryCacheOptions()), NullLogger<ExternalUserMapper>.Instance);

        // Identity AuthenticationType "NTLM" — the client fell back from Kerberos to NTLM.
        var ntlmPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1-1-1-1001"),
            new Claim(ClaimTypes.Name, @"FIRMA\alice"),
        }, authenticationType: "NTLM", ClaimTypes.Name, ClaimTypes.Role));

        var controller = new AuthController(_db, cfg, audit, key, issuer,
            ldapAuthenticator: null, externalUserMapper: mapper,
            ldapOptions: ldapMonitor, windowsOptions: winMonitor)
        {
            ControllerContext = new ControllerContext { HttpContext = NewHttpContext(ntlmPrincipal) },
        };

        var result = await controller.WindowsLogin(default);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
        audit.Calls.Should().Contain(c => c.Action == "LOGIN_FAILED" && c.Details!.Contains("windows_ntlm_disabled"));
        // No JIT (Windows) row should have been created for the NTLM caller — only the
        // seeded local admin remains.
        (await _db.Users.CountAsync(u => u.Provider == AuthProvider.Windows)).Should().Be(0);
    }

    [Fact]
    public async Task WindowsLogin_NtlmIdentity_LegacyAllowNtlmTrue_StillRejected()
    {
        var cfg = NewConfig();
        var key = new TestJwtKeyProvider();
        var audit = new CapturingAuditWriter();
        var issuer = new AuthSessionIssuer(cfg, key, audit);
        var ldapOpts = new LdapOptions { Enabled = false };
        var winOpts = new WindowsAuthOptions { Enabled = true, AllowNtlmFallback = true };
        var ldapMonitor = new StaticOptionsMonitor<LdapOptions>(ldapOpts);
        var winMonitor = new StaticOptionsMonitor<WindowsAuthOptions>(winOpts);
        var mapper = new ExternalUserMapper(_db, ldapMonitor, audit,
            new MemoryCache(new MemoryCacheOptions()), NullLogger<ExternalUserMapper>.Instance);

        var ntlmPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1-1-1-1001"),
            new Claim(ClaimTypes.Name, @"FIRMA\alice"),
        }, authenticationType: "NTLM", ClaimTypes.Name, ClaimTypes.Role));

        var controller = new AuthController(_db, cfg, audit, key, issuer,
            ldapAuthenticator: null, externalUserMapper: mapper,
            ldapOptions: ldapMonitor, windowsOptions: winMonitor)
        {
            ControllerContext = new ControllerContext { HttpContext = NewHttpContext(ntlmPrincipal) },
        };

        var result = await controller.WindowsLogin(default);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
        audit.Calls.Should().Contain(c => c.Action == "LOGIN_FAILED" && c.Details!.Contains("windows_ntlm_disabled"));
    }

    [Fact]
    public async Task WindowsLogin_KerberosIdentity_AllowNtlmFalse_StillAccepted()
    {
        // The fix must not regress the happy path: Kerberos with AllowNtlmFallback=false
        // is the recommended production posture and must work.
        var cfg = NewConfig();
        var key = new TestJwtKeyProvider();
        var audit = new CapturingAuditWriter();
        var issuer = new AuthSessionIssuer(cfg, key, audit);
        var ldapOpts = new LdapOptions
        {
            Enabled = false,
            AllowedGroupSids = ["S-1-5-21-1-1-1-9999"],
        };
        var winOpts = new WindowsAuthOptions { Enabled = true, AllowNtlmFallback = false, NtlmDisabledByPolicy = true };
        var ldapMonitor = new StaticOptionsMonitor<LdapOptions>(ldapOpts);
        var winMonitor = new StaticOptionsMonitor<WindowsAuthOptions>(winOpts);
        var mapper = new ExternalUserMapper(_db, ldapMonitor, audit,
            new MemoryCache(new MemoryCacheOptions()), NullLogger<ExternalUserMapper>.Instance);

        var kerbPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1-1-1-1001"),
            new Claim(ClaimTypes.Name, @"FIRMA\alice"),
        }, authenticationType: "Kerberos", ClaimTypes.Name, ClaimTypes.Role));
        var directory = new FakeAdapter
        {
            Snapshot = new LdapDirectorySnapshot(
                "S-1-5-21-1-1-1-1001", true, "alice@firma.de", "Alice",
                ["S-1-5-21-1-1-1-9999"]),
        };

        var controller = new AuthController(_db, cfg, audit, key, issuer,
            ldapAuthenticator: null, externalUserMapper: mapper,
            ldapOptions: ldapMonitor, windowsOptions: winMonitor,
            directoryAdapter: directory)
        {
            ControllerContext = new ControllerContext { HttpContext = NewHttpContext(kerbPrincipal) },
        };

        var result = await controller.WindowsLogin(default);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    // --- Fix 3: passwordless local rows are never merged ---------------------------

    [Fact]
    public async Task LdapLogin_PreStagedLocalRow_IsNeverMergedIntoLdapIdentity()
    {
        // Migration scenario: an admin pre-staged a local user row with PasswordHash=null
        // expecting that user to authenticate via LDAP. The LDAP login must not
        // short-circuit on the local-row presence — it reaches the mapper, which refuses
        // the collision (identities are never merged automatically) and audits it.
        var prestaged = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice@firma.de",
            Provider = AuthProvider.Local,
            PasswordHash = null,
            Role = UserRole.Operator,
            IsActive = true,
            PasswordChangedAt = DateTime.UtcNow,
        };
        _db.Users.Add(prestaged);
        await _db.SaveChangesAsync();

        var cfg = NewConfig();
        var key = new TestJwtKeyProvider();
        var audit = new CapturingAuditWriter();
        var issuer = new AuthSessionIssuer(cfg, key, audit);
        var ldapOpts = new LdapOptions
        {
            Enabled = true,
            Server = "dc",
            BaseDn = "DC=firma,DC=de",
            UpnSuffix = "firma.de",
            AllowedGroupSids = ["S-1-5-21-1-1-1-9999"],
        };
        var ldapMonitor = new StaticOptionsMonitor<LdapOptions>(ldapOpts);
        var adapter = new FakeAdapter
        {
            Result = new LdapAuthResult(
                "guid-aaa", "alice@firma.de", "Alice", ["S-1-5-21-1-1-1-9999"]),
        };
        var breaker = new LdapCircuitBreaker();
        var ldapAuth = new LdapAuthenticator(ldapMonitor, adapter, breaker, NullLogger<LdapAuthenticator>.Instance);
        var mapper = new ExternalUserMapper(_db, ldapMonitor, audit,
            new MemoryCache(new MemoryCacheOptions()), NullLogger<ExternalUserMapper>.Instance);

        var controller = new AuthController(_db, cfg, audit, key, issuer,
            ldapAuthenticator: ldapAuth, externalUserMapper: mapper, ldapOptions: ldapMonitor)
        {
            ControllerContext = new ControllerContext { HttpContext = NewHttpContext() },
        };

        var result = await controller.Login(new LoginRequest("alice", "pw"), default);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
        var retained = await _db.Users.SingleAsync(u => u.Id == prestaged.Id);
        retained.Provider.Should().Be(AuthProvider.Local);
        retained.ExternalId.Should().BeNull();
        audit.Calls.Should().Contain(c => c.Action == "USER_LDAP_REFUSED_COLLISION");
    }

    // --- Fix 5: empty-password unauthenticated-bind bypass ------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LdapLogin_EmptyPassword_Rejected_EvenIfDirectoryWouldAcceptBind(string password)
    {
        // Auth-bypass regression: AD answers a simple-bind with a populated UPN + empty
        // password as an *unauthenticated bind* (LDAP_SUCCESS), not error 49. The FakeAdapter
        // here is configured to RETURN SUCCESS for any bind — i.e. it simulates that
        // vulnerable directory. The login must still be rejected because the empty/blank
        // password is refused before the adapter is ever consulted, so no session is minted
        // and no LDAP user is JIT-provisioned for the attacker-chosen username.
        var cfg = NewConfig();
        var key = new TestJwtKeyProvider();
        var audit = new CapturingAuditWriter();
        var issuer = new AuthSessionIssuer(cfg, key, audit);
        var ldapOpts = new LdapOptions
        {
            Enabled = true,
            Server = "dc",
            BaseDn = "DC=firma,DC=de",
            UpnSuffix = "firma.de",
        };
        var ldapMonitor = new StaticOptionsMonitor<LdapOptions>(ldapOpts);
        var adapter = new FakeAdapter
        {
            Result = new LdapAuthResult("guid-eve", "eve@firma.de", "Eve", Array.Empty<string>()),
        };
        var ldapAuth = new LdapAuthenticator(ldapMonitor, adapter, new LdapCircuitBreaker(),
            NullLogger<LdapAuthenticator>.Instance);
        var mapper = new ExternalUserMapper(_db, ldapMonitor, audit,
            new MemoryCache(new MemoryCacheOptions()), NullLogger<ExternalUserMapper>.Instance);

        var controller = new AuthController(_db, cfg, audit, key, issuer,
            ldapAuthenticator: ldapAuth, externalUserMapper: mapper, ldapOptions: ldapMonitor)
        {
            ControllerContext = new ControllerContext { HttpContext = NewHttpContext() },
        };

        var result = await controller.Login(new LoginRequest("eve", password), default);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
        // The empty password must not have promoted/created an LDAP identity for "eve".
        (await _db.Users.AnyAsync(u => u.Provider == AuthProvider.Ldap)).Should().BeFalse();
    }

    [Fact]
    public async Task LdapPreJitThrottle_UsesCanonicalUpnAcrossDomainAliases()
    {
        var cfg = NewConfig();
        var key = new TestJwtKeyProvider();
        var audit = new CapturingAuditWriter();
        var issuer = new AuthSessionIssuer(cfg, key, audit);
        var ldapOpts = new LdapOptions
        {
            Enabled = true,
            Server = "dc",
            BaseDn = "DC=firma,DC=de",
            UpnSuffix = "firma.de",
        };
        var monitor = new StaticOptionsMonitor<LdapOptions>(ldapOpts);
        var adapter = new FakeAdapter { Result = null };
        var authenticator = new LdapAuthenticator(
            monitor, adapter, new LdapCircuitBreaker(), NullLogger<LdapAuthenticator>.Instance);
        var mapper = new ExternalUserMapper(_db, monitor, audit,
            new MemoryCache(new MemoryCacheOptions()), NullLogger<ExternalUserMapper>.Instance);
        var throttle = new ExternalLoginThrottle(_db);
        var controller = new AuthController(_db, cfg, audit, key, issuer,
            ldapAuthenticator: authenticator,
            externalUserMapper: mapper,
            ldapOptions: monitor,
            externalLoginThrottle: throttle)
        {
            ControllerContext = new ControllerContext { HttpContext = NewHttpContext() },
        };

        foreach (var alias in new[]
                 {
                     @"A\alice", @"B\alice", "alice", "alice@firma.de", @"C\alice",
                 })
        {
            (await controller.Login(new LoginRequest(alias, "wrong-password"), default)).Result
                .Should().BeOfType<UnauthorizedObjectResult>();
        }

        (await controller.Login(new LoginRequest(@"D\alice", "wrong-password"), default)).Result
            .Should().BeOfType<UnauthorizedObjectResult>();
        adapter.AuthenticateCalls.Should().Be(5,
            "the sixth alias must hit the shared canonical pre-JIT throttle before AD bind");
        audit.Calls.Should().Contain(call => call.Action == "LOGIN_LOCKED"
                                             && call.Details!.Contains("alice@firma.de"));
    }

    [Fact]
    public async Task LdapLogin_LocalRowWithPassword_SkipsLdap()
    {
        // Only a passwordless local row reaches the mapper (for the audited collision
        // refusal). A local user with a real password skips LDAP entirely so an attacker
        // can't probe AD against local accounts.
        var localUser = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice@firma.de",
            Provider = AuthProvider.Local,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password1", AuthController.BCryptWorkFactor),
            Role = UserRole.Admin,
            IsActive = true,
            PasswordChangedAt = DateTime.UtcNow,
        };
        _db.Users.Add(localUser);
        await _db.SaveChangesAsync();

        var cfg = NewConfig();
        var key = new TestJwtKeyProvider();
        var audit = new CapturingAuditWriter();
        var issuer = new AuthSessionIssuer(cfg, key, audit);
        var ldapOpts = new LdapOptions
        {
            Enabled = true,
            Server = "dc",
            BaseDn = "DC=firma,DC=de",
            UpnSuffix = "firma.de",
        };
        var ldapMonitor = new StaticOptionsMonitor<LdapOptions>(ldapOpts);
        var adapter = new FakeAdapter
        {
            Result = new LdapAuthResult("guid-aaa", "alice@firma.de", "Alice", Array.Empty<string>()),
        };
        var breaker = new LdapCircuitBreaker();
        var ldapAuth = new LdapAuthenticator(ldapMonitor, adapter, breaker, NullLogger<LdapAuthenticator>.Instance);
        var mapper = new ExternalUserMapper(_db, ldapMonitor, audit,
            new MemoryCache(new MemoryCacheOptions()), NullLogger<ExternalUserMapper>.Instance);

        var controller = new AuthController(_db, cfg, audit, key, issuer,
            ldapAuthenticator: ldapAuth, externalUserMapper: mapper, ldapOptions: ldapMonitor)
        {
            ControllerContext = new ControllerContext { HttpContext = NewHttpContext() },
        };

        // Wrong local password — the local path returns 401, LDAP must NOT have been
        // attempted and must NOT have promoted this row.
        var result = await controller.Login(new LoginRequest("alice@firma.de", "wrong"), default);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
        var stillLocal = await _db.Users.SingleAsync(u => u.Id == localUser.Id);
        stillLocal.Provider.Should().Be(AuthProvider.Local);
        audit.Calls.Should().NotContain(c => c.Action == "USER_LDAP_LINKED");
    }
}
