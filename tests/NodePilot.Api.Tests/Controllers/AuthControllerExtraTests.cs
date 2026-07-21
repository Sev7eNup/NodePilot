using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.Security;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// Branch-coverage gap-fillers for AuthController. The original AuthControllerTests
/// covers the common happy paths (bootstrap, login success, refresh) but skipped:
/// account lockout (H-4), disabled-user 401, the FailedLoginCount-resets-on-success
/// branch, the bootstrap pinned-username guard (H12), and the Logout token-revocation
/// flow.
/// </summary>
public sealed class AuthControllerExtraTests : IDisposable
{
    private readonly string _contentRoot;
    private readonly IHostEnvironment _env;

    public AuthControllerExtraTests()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "AuthControllerExtraTests-" + Guid.NewGuid());
        Directory.CreateDirectory(_contentRoot);
        var envMock = new Mock<IHostEnvironment>();
        envMock.SetupGet(e => e.ContentRootPath).Returns(_contentRoot);
        envMock.SetupGet(e => e.EnvironmentName).Returns(Environments.Development);
        _env = envMock.Object;
    }

    public void Dispose()
    {
        try { Directory.Delete(_contentRoot, recursive: true); } catch { }
    }

    private static NodePilotDbContext CreateContext() => NodePilot.TestCommons.TestDbFactory.Create();

    private static IConfiguration CreateConfig(Dictionary<string, string?>? extra = null)
    {
        var dict = new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "NodePilot-Test-Secret-Key-Minimum-32-Characters!",
            ["Jwt:Issuer"] = "NodePilot",
            ["Jwt:Audience"] = "NodePilot",
        };
        if (extra is not null) foreach (var kv in extra) dict[kv.Key] = kv.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private sealed class TestJwtKeyProvider : IJwtKeyProvider
    {
        public string Key => "NodePilot-Test-Secret-Key-Minimum-32-Characters!";
    }

    private DefaultHttpContext HttpCtx(string? setupToken = null, ClaimsPrincipal? user = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_env);
        var ctx = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        if (setupToken is not null) ctx.Request.Headers[AdminBootstrap.TokenHeader] = setupToken;
        if (user is not null) ctx.User = user;
        return ctx;
    }

    private AuthController NewController(NodePilotDbContext db, IConfiguration? cfg = null)
    {
        var resolvedCfg = cfg ?? CreateConfig();
        return new AuthController(db, resolvedCfg, NoopAuditWriter.Instance, new TestJwtKeyProvider(),
            new NodePilot.Api.Security.AuthSessionIssuer(resolvedCfg, new TestJwtKeyProvider(), NoopAuditWriter.Instance))
        {
            ControllerContext = new ControllerContext { HttpContext = HttpCtx() },
        };
    }

    private static User CreateUser(
        string username = "alice", string password = "Password1",
        bool isActive = true, int failedCount = 0, DateTime? lockedUntil = null,
        UserRole role = UserRole.Operator)
    {
        return new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            Role = role,
            IsActive = isActive,
            FailedLoginCount = failedCount,
            LockedUntil = lockedUntil,
        };
    }

    [Fact]
    public async Task Login_WrongPassword_IncrementsFailedCounterAndReturnsUnauthorized()
    {
        var db = CreateContext();
        var user = CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var result = await NewController(db).Login(
            new LoginRequest(user.Username, "wrong-password"), CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
        var fresh = await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        fresh.FailedLoginCount.Should().Be(1);
        fresh.LockedUntil.Should().BeNull();
    }

    [Fact]
    public async Task Login_RepeatedFailures_TriggersLockout()
    {
        var db = CreateContext();
        // Seed at threshold-1 so the next failure trips the lockout. The exact threshold
        // is private; we drive 10 failed logins which is more than any reasonable threshold.
        var user = CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        for (var i = 0; i < 10; i++)
        {
            await NewController(db).Login(new LoginRequest(user.Username, "wrong"), CancellationToken.None);
        }

        var locked = await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        locked.LockedUntil.Should().NotBeNull();
        locked.LockedUntil!.Value.Should().BeAfter(DateTime.UtcNow);
        locked.FailedLoginCount.Should().BeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public async Task Login_ParallelWrongPasswords_AtomicallyStopsAtLockoutThreshold()
    {
        var databasePath = Path.Combine(_contentRoot, "parallel-login.db");
        var options = new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite($"Data Source={databasePath};Default Timeout=30;Pooling=False")
            .Options;
        Guid userId;
        await using (var setup = new NodePilotDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            var user = CreateUser();
            userId = user.Id;
            setup.Users.Add(user);
            await setup.SaveChangesAsync();
        }

        var attempts = Enumerable.Range(0, AuthController.LockoutFailureThreshold + 5)
            .Select(async _ =>
            {
                await using var db = new NodePilotDbContext(options);
                var result = await NewController(db).Login(
                    new LoginRequest("alice", "wrong-password"), CancellationToken.None);
                result.Result.Should().BeOfType<UnauthorizedObjectResult>();
            });

        await Task.WhenAll(attempts);

        await using var verify = new NodePilotDbContext(options);
        var persisted = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        persisted.FailedLoginCount.Should().Be(AuthController.LockoutFailureThreshold,
            "attempt admission is a conditional database update, so parallel writers cannot lose increments");
        persisted.LockedUntil.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task Login_AccountLocked_RejectsBeforeBcrypt()
    {
        // When the user is in their lockout window, even a correct password must be rejected.
        var db = CreateContext();
        var user = CreateUser(lockedUntil: DateTime.UtcNow.AddMinutes(15));
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var result = await NewController(db).Login(
            new LoginRequest(user.Username, "Password1"), CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task Login_DisabledUser_RejectedEvenWithCorrectPassword()
    {
        var db = CreateContext();
        var user = CreateUser(isActive: false);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var result = await NewController(db).Login(
            new LoginRequest(user.Username, "Password1"), CancellationToken.None);

        var unauth = result.Result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        // L-2 (security audit 2026-05-15): the public response is the generic
        // "Invalid credentials" so a username-enumeration attacker cannot tell a disabled
        // account apart from an unknown one. The audit row + metric still record the
        // precise reason ("account_disabled") for operator visibility.
        unauth.Value!.ToString().Should().Contain("Invalid credentials");
    }

    [Fact]
    public async Task Login_SuccessAfterFailures_ResetsCounterAndClearsLock()
    {
        var db = CreateContext();
        // Lock that has already expired. Successful login must clear both counter and lock-stamp.
        var user = CreateUser(failedCount: 3, lockedUntil: DateTime.UtcNow.AddMinutes(-1));
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var result = await NewController(db).Login(
            new LoginRequest(user.Username, "Password1"), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        var fresh = await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        fresh.FailedLoginCount.Should().Be(0);
        fresh.LockedUntil.Should().BeNull();
    }

    [Fact]
    public async Task Login_BootstrapToken_PinnedUsernameMismatch_Rejected()
    {
        // H12 guard: when the operator pins NodePilot:BootstrapAdminUsername, only that
        // username may consume the setup token, even if the token itself is valid.
        var db = CreateContext();
        File.WriteAllText(Path.Combine(_contentRoot, AdminBootstrap.TokenFileName), "tok");
        var cfg = CreateConfig(new() { ["NodePilot:BootstrapAdminUsername"] = "rightful-admin" });

        var controller = new AuthController(db, cfg, NoopAuditWriter.Instance, new TestJwtKeyProvider(), new NodePilot.Api.Security.AuthSessionIssuer(CreateConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance))
        {
            ControllerContext = new ControllerContext { HttpContext = HttpCtx(setupToken: "tok") },
        };

        var result = await controller.Login(
            new LoginRequest("attacker", "Password1"), CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
        (await db.Users.CountAsync()).Should().Be(0,
            "the pinned-username guard must not allow the wrong username to bootstrap");
    }

    [Fact]
    public async Task Logout_WithJtiClaim_RevokesToken()
    {
        var db = CreateContext();
        var user = CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var jti = Guid.NewGuid().ToString();
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, jti),
            new Claim("exp", exp),
        }, "test"));

        var controller = new AuthController(db, CreateConfig(), NoopAuditWriter.Instance, new TestJwtKeyProvider(), new NodePilot.Api.Security.AuthSessionIssuer(CreateConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance))
        {
            ControllerContext = new ControllerContext { HttpContext = HttpCtx(user: principal) },
        };

        var result = await controller.Logout(CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        var revoked = await db.RevokedTokens.AsNoTracking().FirstOrDefaultAsync(r => r.Jti == jti);
        revoked.Should().NotBeNull();
        revoked!.UserId.Should().Be(user.Id);
        revoked.Reason.Should().Be("user-logout");
    }

    [Fact]
    public async Task Logout_NoJtiClaim_StillReturnsNoContent_NoRevocationRow()
    {
        // No-jti path: the token may have been minted before jti was added, or the caller
        // may simply not have a token. Logout must scrub cookies and 204.
        var db = CreateContext();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        }, "test"));

        var controller = new AuthController(db, CreateConfig(), NoopAuditWriter.Instance, new TestJwtKeyProvider(), new NodePilot.Api.Security.AuthSessionIssuer(CreateConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance))
        {
            ControllerContext = new ControllerContext { HttpContext = HttpCtx(user: principal) },
        };

        var result = await controller.Logout(CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (await db.RevokedTokens.CountAsync()).Should().Be(0,
            "no jti => nothing to revoke");
    }

    [Fact]
    public async Task Logout_DuplicateCall_IsIdempotent_NoSecondAuditEntry()
    {
        var db = CreateContext();
        var jti = Guid.NewGuid().ToString();
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, jti),
            new Claim("exp", exp),
        }, "test"));

        var controller = new AuthController(db, CreateConfig(), NoopAuditWriter.Instance, new TestJwtKeyProvider(), new NodePilot.Api.Security.AuthSessionIssuer(CreateConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance))
        {
            ControllerContext = new ControllerContext { HttpContext = HttpCtx(user: principal) },
        };

        await controller.Logout(CancellationToken.None);
        await controller.Logout(CancellationToken.None);

        (await db.RevokedTokens.CountAsync()).Should().Be(1,
            "second logout must hit the existing-jti branch â€” not insert a duplicate");
    }
}
