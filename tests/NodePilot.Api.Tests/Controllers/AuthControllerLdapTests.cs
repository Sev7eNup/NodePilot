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
/// Coverage for phase PR4 of the LDAP/Windows-SSO rollout: AuthController.Login with LDAP
/// wired in. Builds a real AuthController
/// against an in-memory SQLite DB, a real LdapAuthenticator + ExternalUserMapper, and a
/// fake ILdapConnectionAdapter so we can program directory verdicts deterministically.
/// </summary>
public sealed class AuthControllerLdapTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _conn;
    private readonly NodePilotDbContext _db;

    private readonly string _contentRoot;
    private readonly IHostEnvironment _env;

    public AuthControllerLdapTests()
    {
        var (conn, db) = NodePilot.TestCommons.TestDbFactory.CreateWithConnection();
        _conn = conn;
        _db = db;
        _contentRoot = Path.Combine(Path.GetTempPath(), "AuthControllerLdapTests-" + Guid.NewGuid());
        Directory.CreateDirectory(_contentRoot);
        var envMock = new Mock<IHostEnvironment>();
        envMock.SetupGet(e => e.ContentRootPath).Returns(_contentRoot);
        envMock.SetupGet(e => e.EnvironmentName).Returns("Test");
        _env = envMock.Object;

        // Rollout phase PR10 added a rule: the empty-DB bootstrap gate now refuses external
        // just-in-time (JIT) user provisioning for non-Admin roles. Seed an admin so these
        // tests exercise the post-bootstrap mainline (which is what they were written to
        // cover). The dedicated bootstrap-gate tests live in the PR10 regression file.
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

    private DefaultHttpContext NewHttpContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_env);
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }

    private const string TestSecret = "NodePilot-Test-Secret-Key-Minimum-32-Characters!";

    private sealed class FakeAdapter : ILdapConnectionAdapter
    {
        public LdapAuthResult? Result { get; set; }
        public bool ThrowInfra { get; set; }
        public Exception? ExceptionToThrow { get; set; }
        public int Calls { get; private set; }
        public Task<LdapAuthResult?> AuthenticateAsync(string upn, string password, CancellationToken ct)
        {
            Calls++;
            if (ExceptionToThrow is not null) throw ExceptionToThrow;
            if (ThrowInfra) throw new LdapInfrastructureException("simulated DC offline");
            return Task.FromResult(Result);
        }
    }

    private sealed class SlowRejectingAdapter : ILdapConnectionAdapter
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public async Task<LdapAuthResult?> AuthenticateAsync(string upn, string password, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(100, ct);
            return null;
        }
    }

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

    private static LdapOptions EnabledOptions() => new()
    {
        Enabled = true,
        Server = "dc.local",
        BaseDn = "DC=firma,DC=de",
        UpnSuffix = "firma.de",
        AllowedGroupSids =
        [
            "S-1-5-21-1-1-1-512",
            "S-1-5-21-1-1-1-513",
        ],
    };

    private (AuthController controller, FakeAdapter adapter) NewController(
        LdapOptions? options = null, CapturingAuditWriter? audit = null)
    {
        var cfg = NewConfig();
        var key = new TestJwtKeyProvider();
        var auditWriter = (NodePilot.Core.Audit.IAuditWriter)(audit ?? (NodePilot.Core.Audit.IAuditWriter)NoopAuditWriter.Instance);
        var issuer = new AuthSessionIssuer(cfg, key, auditWriter);

        var optsMonitor = new StaticOptionsMonitor<LdapOptions>(options ?? EnabledOptions());
        var adapter = new FakeAdapter();
        var breaker = new LdapCircuitBreaker();
        var ldapAuth = new LdapAuthenticator(optsMonitor, adapter, breaker, NullLogger<LdapAuthenticator>.Instance);
        var mapper = new ExternalUserMapper(_db, optsMonitor, auditWriter,
            new MemoryCache(new MemoryCacheOptions()), NullLogger<ExternalUserMapper>.Instance);

        var controller = new AuthController(_db, cfg, auditWriter, key, issuer,
            ldapAuthenticator: ldapAuth,
            externalUserMapper: mapper,
            ldapOptions: optsMonitor)
        {
            ControllerContext = new ControllerContext { HttpContext = NewHttpContext() },
        };
        return (controller, adapter);
    }

    [Fact]
    public async Task LdapSuccess_NewUser_JitProvisionsAndReturnsToken()
    {
        var (controller, adapter) = NewController();
        // Programmatic caller opt-in so the JWT is echoed in the body (LDAP is password-gated).
        controller.Request.Headers[AuthController.TokenResponseHeader] = "true";
        adapter.Result = new LdapAuthResult(
            ExternalId: "guid-aaa",
            Upn: "alice@firma.de",
            DisplayName: "Alice Example",
            GroupSids: new[] { "S-1-5-21-1-1-1-512" });

        var result = await controller.Login(new LoginRequest("alice", "pw"), default);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var login = ok.Value.Should().BeAssignableTo<LoginResponse>().Subject;
        login.Username.Should().Be("alice@firma.de");

        // JIT row created with Provider=Ldap.
        var persisted = await _db.Users.SingleAsync(u => u.Provider == AuthProvider.Ldap);
        persisted.Provider.Should().Be(AuthProvider.Ldap);
        persisted.ExternalId.Should().Be("guid-aaa");
    }

    [Fact]
    public async Task LdapSuccess_LastAdminWouldBeDemoted_RefusesLogin()
    {
        _db.Users.RemoveRange(_db.Users);
        _db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = "alice@firma.de",
            Provider = AuthProvider.Ldap,
            ExternalId = "guid-aaa",
            KnownGroupSidsJson = "[\"S-1-5-21-1-1-1-512\"]",
            Role = UserRole.Admin,
            IsActive = true,
            SecurityStamp = 4,
        });
        await _db.SaveChangesAsync();

        var audit = new CapturingAuditWriter();
        var (controller, adapter) = NewController(audit: audit);
        controller.Request.Headers[AuthController.TokenResponseHeader] = "true";
        adapter.Result = new LdapAuthResult(
            "guid-aaa", "alice@firma.de", "Alice", ["S-1-5-21-1-1-1-513"]);

        var result = await controller.Login(new LoginRequest("alice", "pw"), default);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
        controller.Response.Headers.SetCookie.ToString().Should().NotContain("np_auth=");
        (await _db.AuditLog.CountAsync(c => c.Action == "USER_LDAP_REFUSED_LAST_ADMIN"))
            .Should().Be(1);
    }

    [Fact]
    public async Task LdapSuccess_GroupsStayServerSideAndOutOfToken()
    {
        var (controller, adapter) = NewController();
        controller.Request.Headers[AuthController.TokenResponseHeader] = "true";
        adapter.Result = new LdapAuthResult(
            "guid-aaa", "alice@firma.de", "Alice",
            new[] { "S-1-5-21-1-1-1-512", "S-1-5-21-1-1-1-513" });

        var result = await controller.Login(new LoginRequest("alice", "pw"), default);

        var login = ((OkObjectResult)result.Result!).Value as LoginResponse;
        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(login!.Token);

        token.Claims.Should().NotContain(c => c.Type == System.Security.Claims.ClaimTypes.GroupSid);
        var userId = Guid.Parse(token.Claims.Single(c => c.Type == ClaimTypes.NameIdentifier).Value);
        (await _db.DirectoryMemberships
                .Where(m => m.UserId == userId)
                .Select(m => m.GroupKey)
                .ToListAsync())
            .Should().BeEquivalentTo("S-1-5-21-1-1-1-512", "S-1-5-21-1-1-1-513");
    }

    [Fact]
    public async Task LdapInvalidCredentials_NoLocalUser_Returns401()
    {
        var (controller, adapter) = NewController();
        adapter.Result = null; // wrong password

        var result = await controller.Login(new LoginRequest("alice", "wrong"), default);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
        // No new (LDAP) row was created — only the seeded admin remains.
        (await _db.Users.CountAsync(u => u.Provider == AuthProvider.Ldap)).Should().Be(0);
    }

    [Fact]
    public async Task Login_EmptyDbWithSetupTokenHeader_SkipsLdapForBootstrap()
    {
        // With LDAP enabled the LDAP-first bind would otherwise intercept the one-shot
        // bootstrap login (unknown username → InvalidCredentials/503), making an instance
        // with LDAP already on impossible to initialise. A presented X-Setup-Token on an
        // empty Users table must therefore bypass LDAP and reach the local bootstrap path.
        _db.Users.RemoveRange(_db.Users);
        await _db.SaveChangesAsync();

        var (controller, adapter) = NewController();
        controller.Request.Headers[NodePilot.Api.Security.AdminBootstrap.TokenHeader] = "any-token";

        await controller.Login(new LoginRequest("admin", "pw"), default);

        adapter.Calls.Should().Be(0,
            "a presented setup token on an empty DB must reach local bootstrap, not the LDAP bind");
    }

    [Fact]
    public async Task Login_EmptyDbWithoutSetupToken_StillAttemptsLdap()
    {
        // The bootstrap bypass is gated on the header — without it the LDAP-first path is
        // unchanged, so a normal (non-bootstrap) login still probes the directory.
        _db.Users.RemoveRange(_db.Users);
        await _db.SaveChangesAsync();

        var (controller, adapter) = NewController();
        adapter.Result = null; // directory rejects

        await controller.Login(new LoginRequest("alice", "pw"), default);

        adapter.Calls.Should().Be(1, "without a setup token the LDAP-first path is unchanged");
    }

    [Fact]
    public async Task LdapCancellation_ReleasesPreJitReservation()
    {
        var (controller, adapter) = NewController();
        adapter.ExceptionToThrow = new OperationCanceledException("client disconnected");

        Func<Task> act = async () => await controller.Login(
            new LoginRequest("alice", "wrong"), CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await _db.IdempotencyKeys.CountAsync()).Should().Be(0,
            "an aborted request produced no credential verdict");
    }

    [Fact]
    public async Task LdapUnexpectedFailure_ReleasesSharedAndPersistedReservations()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice@firma.de",
            Provider = AuthProvider.Ldap,
            ExternalId = "guid-alice",
            Role = UserRole.Viewer,
            IsActive = true,
            FailedLoginCount = 4,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        var (controller, adapter) = NewController();
        adapter.ExceptionToThrow = new InvalidOperationException("unexpected adapter failure");

        Func<Task> act = async () => await controller.Login(
            new LoginRequest("alice", "wrong"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _db.Entry(user).ReloadAsync();
        user.FailedLoginCount.Should().Be(4);
        user.LockedUntil.Should().BeNull();
        (await _db.IdempotencyKeys.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task OverlongUsername_IsRejectedBeforeLdapOrThrottleWork()
    {
        var (controller, adapter) = NewController();

        var result = await controller.Login(
            new LoginRequest(new string('a', ExternalLoginThrottle.MaximumUsernameLength + 1), "wrong"),
            CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
        adapter.Calls.Should().Be(0);
        (await _db.IdempotencyKeys.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ParallelPreJitLogins_AcrossDbContexts_OnlyFiveReachDirectory()
    {
        var databasePath = Path.Combine(_contentRoot, "parallel-ldap.db");
        var dbOptions = new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite($"Data Source={databasePath};Default Timeout=30;Pooling=False")
            .Options;
        await using (var setup = new NodePilotDbContext(dbOptions))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                Username = "admin",
                PasswordHash = "$2a$12$dummy",
                Provider = AuthProvider.Local,
                Role = UserRole.Admin,
                IsActive = true,
            });
            await setup.SaveChangesAsync();
        }

        var adapter = new SlowRejectingAdapter();
        var ldapOptions = EnabledOptions();
        var monitor = new StaticOptionsMonitor<LdapOptions>(ldapOptions);
        var attempts = Enumerable.Range(0, 10).Select(async i =>
        {
            await using var db = new NodePilotDbContext(dbOptions);
            var cfg = NewConfig();
            var key = new TestJwtKeyProvider();
            var issuer = new AuthSessionIssuer(cfg, key, NoopAuditWriter.Instance);
            var authenticator = new LdapAuthenticator(
                monitor, adapter, new LdapCircuitBreaker(), NullLogger<LdapAuthenticator>.Instance);
            var mapper = new ExternalUserMapper(db, monitor, NoopAuditWriter.Instance,
                new MemoryCache(new MemoryCacheOptions()), NullLogger<ExternalUserMapper>.Instance);
            var controller = new AuthController(db, cfg, NoopAuditWriter.Instance, key, issuer,
                ldapAuthenticator: authenticator,
                externalUserMapper: mapper,
                ldapOptions: monitor)
            {
                ControllerContext = new ControllerContext { HttpContext = NewHttpContext() },
            };

            var alias = i % 2 == 0 ? "alice" : "ALICE@FIRMA.DE";
            var result = await controller.Login(new LoginRequest(alias, "wrong"), default);
            result.Result.Should().BeOfType<UnauthorizedObjectResult>();
        });

        await Task.WhenAll(attempts);

        adapter.Calls.Should().Be(5,
            "the shared database's five unique attempt slots must cap a cross-node concurrent burst");
    }

    [Fact]
    public async Task LdapInvalidCredentials_RatchetsFailureCount_AndLocksAtThreshold()
    {
        // M-2 (security audit 2026-05-15): the LDAP path must apply the same per-account lockout
        // as the local path. Without it the only LDAP guard was the service-wide circuit breaker,
        // which trips on infra failures — NOT credential rejections — so a known account could be
        // brute-forced indefinitely. One more rejection at the threshold must lock the account.
        var ldapUser = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice@firma.de",
            Provider = AuthProvider.Ldap,
            ExternalId = "guid-alice",
            Role = UserRole.Operator,
            IsActive = true,
            FailedLoginCount = AuthController.LockoutFailureThreshold - 1,
        };
        _db.Users.Add(ldapUser);
        await _db.SaveChangesAsync();

        var audit = new CapturingAuditWriter();
        var (controller, adapter) = NewController(audit: audit);
        adapter.Result = null; // directory rejects the credentials

        var result = await controller.Login(new LoginRequest("alice", "wrong"), default);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
        await _db.Entry(ldapUser).ReloadAsync();
        ldapUser.FailedLoginCount.Should().Be(AuthController.LockoutFailureThreshold);
        ldapUser.LockedUntil.Should().NotBeNull("the LDAP account must lock once it hits the failure threshold");
        audit.Calls.Should().Contain(c => c.Action == "LOGIN_LOCKED");
    }

    [Fact]
    public async Task LdapLogin_WhenAccountAlreadyLocked_RefusedBeforeDirectoryCall()
    {
        // M-2: a locked LDAP account is refused before the directory is even consulted — even
        // when the (would-be) credentials are valid. The valid adapter result below would mint a
        // token if the lock gate did not run first.
        _db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = "alice@firma.de",
            Provider = AuthProvider.Ldap,
            ExternalId = "guid-alice",
            Role = UserRole.Operator,
            IsActive = true,
            LockedUntil = DateTime.UtcNow.AddMinutes(10),
        });
        await _db.SaveChangesAsync();

        var audit = new CapturingAuditWriter();
        var (controller, adapter) = NewController(audit: audit);
        adapter.Result = new LdapAuthResult("guid-alice", "alice@firma.de", "Alice",
            new[] { "S-1-5-21-1-1-1-512" });

        var result = await controller.Login(new LoginRequest("alice", "correct-pw"), default);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>("a locked account is refused before the directory call");
        audit.Calls.Should().Contain(c => c.Action == "LOGIN_LOCKED");
        (await _db.Users.CountAsync(u => u.Provider == AuthProvider.Ldap)).Should().Be(1, "no JIT row should be created for a blocked login");
    }

    [Fact]
    public async Task LdapUnavailable_ExternalCandidate_Returns503()
    {
        // A password-bearing local row is short-circuited before LDAP. Any request that
        // reaches an unavailable directory cannot establish current external authorization.
        var (controller, adapter) = NewController();
        adapter.ThrowInfra = true;

        var result = await controller.Login(new LoginRequest("alice", "pw"), default);

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task LdapDisabled_FallsThroughToLocal()
    {
        // Standard local user + LDAP disabled in config: behaves identically to a build
        // without LDAP wired in.
        _db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = "alice",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password1", AuthController.BCryptWorkFactor),
            Provider = AuthProvider.Local,
            Role = UserRole.Operator,
            IsActive = true,
            PasswordChangedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var (controller, _) = NewController(new LdapOptions { Enabled = false });
        var result = await controller.Login(new LoginRequest("alice", "Password1"), default);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task LdapEnabled_LocalOnlyUserExists_SkipsLdap_FallsThroughToLocal()
    {
        // A local user with the same Username exists. LDAP must not even be probed —
        // protects local-only admins during a DC outage and prevents a username-squat
        // attacker from poking AD with bench-test passwords against the local namespace.
        _db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = "admin",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password1", AuthController.BCryptWorkFactor),
            Provider = AuthProvider.Local,
            Role = UserRole.Admin,
            IsActive = true,
            PasswordChangedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var (controller, adapter) = NewController();
        adapter.Result = null; // would normally produce 401 if LDAP were probed
        var result = await controller.Login(new LoginRequest("admin", "Password1"), default);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task LdapInvalidCredentials_DoesNotFallThroughOnUnknownUser()
    {
        // No local user matches the input. LDAP says no. Don't try to BCrypt against
        // a non-existent local user — but the existing local path already handles that
        // by returning 401. We assert the response is 401 with the LDAP failure reason
        // (not the bootstrap branch's "Admin bootstrap required").
        var auditCapture = new CapturingAuditWriter();
        var (controller, adapter) = NewController(audit: auditCapture);
        adapter.Result = null;

        var result = await controller.Login(new LoginRequest("eve", "wrong"), default);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
        auditCapture.Calls
            .Should().Contain(c => c.Action == "LOGIN_FAILED" && c.Details!.Contains("ldap_invalid_credentials"));
    }

    [Fact]
    public async Task LdapSuccess_DeactivatedUser_Returns401()
    {
        // Pre-existing JIT row with IsActive=false. Mapper preserves the deactivation.
        // AuthController must reject before minting a session.
        _db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = "alice@firma.de",
            Provider = AuthProvider.Ldap,
            ExternalId = "guid-aaa",
            Role = UserRole.Viewer,
            IsActive = false,
            PasswordChangedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var (controller, adapter) = NewController();
        adapter.Result = new LdapAuthResult("guid-aaa", "alice@firma.de", "Alice", Array.Empty<string>());

        var result = await controller.Login(new LoginRequest("alice", "pw"), default);

        var unauth = result.Result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        // Security-audit finding L-2: generic "Invalid credentials" response — see AuthControllerExtraTests
        // commentary on Login_DisabledUser_RejectedEvenWithCorrectPassword.
        unauth.Value!.ToString()!.Should().Contain("Invalid credentials");
    }
}
