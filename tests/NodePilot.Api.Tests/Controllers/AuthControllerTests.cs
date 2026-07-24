using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.Security;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public sealed class AuthControllerTests : IDisposable
{
    private readonly string _contentRoot;
    private readonly IHostEnvironment _env;

    public AuthControllerTests()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "AuthControllerTests-" + Guid.NewGuid());
        Directory.CreateDirectory(_contentRoot);
        var envMock = new Mock<IHostEnvironment>();
        envMock.SetupGet(e => e.ContentRootPath).Returns(_contentRoot);
        envMock.SetupGet(e => e.EnvironmentName).Returns(Environments.Development);
        _env = envMock.Object;
    }

    public void Dispose()
    {
        try { Directory.Delete(_contentRoot, recursive: true); } catch { /* best-effort */ }
    }

    private string WriteBootstrapToken(string token)
    {
        var path = Path.Combine(_contentRoot, AdminBootstrap.TokenFileName);
        File.WriteAllText(path, token);
        return path;
    }

    private DefaultHttpContext HttpCtx(string? setupToken = null, bool tokenInBody = false)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, _env);
        var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var ctx = new DefaultHttpContext { RequestServices = provider };
        if (setupToken is not null) ctx.Request.Headers[AdminBootstrap.TokenHeader] = setupToken;
        // Programmatic (non-browser) caller opt-in: makes the login paths echo the JWT back in
        // the body, mirroring how the CLI authenticates.
        if (tokenInBody) ctx.Request.Headers[AuthController.TokenResponseHeader] = "true";
        return ctx;
    }

    private static NodePilotDbContext CreateContext() => NodePilot.TestCommons.TestDbFactory.Create();

    private static IConfiguration CreateConfig()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "NodePilot-Test-Secret-Key-Minimum-32-Characters!",
                ["Jwt:Issuer"] = "NodePilot",
                ["Jwt:Audience"] = "NodePilot",
                ["Authentication:LocalLoginMode"] = "Enabled",
            })
            .Build();
    }

    // Security-audit finding M-2: tests used to rely on AuthController reading Jwt:Key from
    // IConfiguration per request. The production path now injects IJwtKeyProvider (validated once at startup).
    // TestJwtKeyProvider mirrors that contract with the same key value CreateConfig emits.
    private sealed class TestJwtKeyProvider : IJwtKeyProvider
    {
        public string Key => "NodePilot-Test-Secret-Key-Minimum-32-Characters!";
    }

    [Fact]
    public async Task Login_FirstUser_WithValidBootstrapToken_CreatesAdmin()
    {
        // Arrange
        var db = CreateContext();
        var config = CreateConfig();
        WriteBootstrapToken("correct-token");
        var controller = new AuthController(db, config, NoopAuditWriter.Instance, new TestJwtKeyProvider(), new NodePilot.Api.Security.AuthSessionIssuer(config ?? CreateConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance))
        {
            ControllerContext = new ControllerContext { HttpContext = HttpCtx(setupToken: "correct-token", tokenInBody: true) },
        };
        var request = new LoginRequest("admin", "password123");

        // Act
        var result = await controller.Login(request, CancellationToken.None);

        // Assert
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<LoginResponse>().Subject;
        response.Token.Should().NotBeNullOrEmpty();
        response.Username.Should().Be("admin");
        response.Role.Should().Be("Admin");

        // Verify user was created and the token file was consumed
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == "admin");
        user.Should().NotBeNull();
        user!.Role.Should().Be(UserRole.Admin);
        user.IsBreakGlass.Should().BeTrue("the bootstrap admin is the default recovery account");
        File.Exists(Path.Combine(_contentRoot, AdminBootstrap.TokenFileName)).Should().BeFalse(
            "the bootstrap token must be deleted after successful first-admin creation");
    }

    [Fact]
    public async Task Login_ParallelBootstrapAttempts_AcrossDbContexts_CreateExactlyOneAdmin()
    {
        var databasePath = Path.Combine(_contentRoot, "parallel-bootstrap.db");
        var options = new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite($"Data Source={databasePath};Default Timeout=30;Pooling=False")
            .Options;
        await using (var setup = new NodePilotDbContext(options))
            await setup.Database.EnsureCreatedAsync();

        WriteBootstrapToken("shared-bootstrap-token");

        async Task<IActionResult?> AttemptAsync(string username)
        {
            await using var db = new NodePilotDbContext(options);
            var config = CreateConfig();
            var key = new TestJwtKeyProvider();
            var controller = new AuthController(
                db,
                config,
                NoopAuditWriter.Instance,
                key,
                new AuthSessionIssuer(config, key, NoopAuditWriter.Instance))
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = HttpCtx(
                        setupToken: "shared-bootstrap-token",
                        tokenInBody: true),
                },
            };

            return (await controller.Login(
                new LoginRequest(username, "password123"), CancellationToken.None)).Result;
        }

        var results = await Task.WhenAll(
            AttemptAsync("first-admin"),
            AttemptAsync("second-admin"));

        results.Count(result => result is OkObjectResult).Should().Be(1);
        results.Count(result => result is UnauthorizedObjectResult).Should().Be(1);

        await using var verify = new NodePilotDbContext(options);
        var persisted = await verify.Users.AsNoTracking().SingleAsync();
        persisted.Role.Should().Be(UserRole.Admin);
        persisted.IsBreakGlass.Should().BeTrue();
    }

    [Fact]
    public async Task Login_FirstUser_WithoutBootstrapToken_RejectsWithoutCreatingUser()
    {
        // Arrange
        var db = CreateContext();
        var config = CreateConfig();
        WriteBootstrapToken("correct-token");
        var audit = new CapturingAuditWriter();
        var controller = new AuthController(db, config, audit, new TestJwtKeyProvider(), new NodePilot.Api.Security.AuthSessionIssuer(config ?? CreateConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance))
        {
            ControllerContext = new ControllerContext { HttpContext = HttpCtx(setupToken: null) },
        };

        // Act
        var result = await controller.Login(new LoginRequest("attacker", "x"), CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
        (await db.Users.AnyAsync()).Should().BeFalse("no user must be created without a valid setup token");
        audit.Calls.Should().ContainSingle(call =>
            call.Action == AuditActions.LoginFailed
            && call.Details!.Contains("\"reason\":\"bootstrap_token_invalid\""));
    }

    [Fact]
    public async Task Login_FirstUser_WithWrongBootstrapToken_Rejects()
    {
        var db = CreateContext();
        var config = CreateConfig();
        WriteBootstrapToken("correct-token");
        var audit = new CapturingAuditWriter();
        var controller = new AuthController(db, config, audit, new TestJwtKeyProvider(), new NodePilot.Api.Security.AuthSessionIssuer(config ?? CreateConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance))
        {
            ControllerContext = new ControllerContext { HttpContext = HttpCtx(setupToken: "wrong") },
        };

        var result = await controller.Login(new LoginRequest("attacker", "x"), CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
        (await db.Users.AnyAsync()).Should().BeFalse();
        audit.Calls.Should().ContainSingle(call =>
            call.Action == AuditActions.LoginFailed
            && call.Details!.Contains("\"reason\":\"bootstrap_token_invalid\""));
    }

    [Fact]
    public async Task Login_LocalModeDisabled_RejectsValidLocalPassword()
    {
        var db = CreateContext();
        var user = new User
        {
            Id = Guid.NewGuid(), Username = "admin",
            Provider = AuthProvider.Local,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("password123", AuthController.BCryptWorkFactor),
            Role = UserRole.Admin, IsActive = true, IsBreakGlass = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var config = CreateConfig();
        var issuer = new AuthSessionIssuer(config, new TestJwtKeyProvider(), NoopAuditWriter.Instance);
        var controller = new AuthController(
            db, config, NoopAuditWriter.Instance, new TestJwtKeyProvider(), issuer,
            activeAuthentication: new ActiveAuthenticationConfiguration(
                LocalLoginMode.Disabled, false, false, false, "Single Sign-On"))
        {
            ControllerContext = new ControllerContext { HttpContext = HttpCtx(tokenInBody: true) },
        };

        var result = await controller.Login(new LoginRequest("admin", "password123"), default);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task Login_BreakGlassOnly_AllowsOnlyFlaggedLocalAccount()
    {
        var db = CreateContext();
        var passwordHash = BCrypt.Net.BCrypt.HashPassword("password123", AuthController.BCryptWorkFactor);
        db.Users.AddRange(
            new User
            {
                Id = Guid.NewGuid(), Username = "recovery", Provider = AuthProvider.Local,
                PasswordHash = passwordHash, Role = UserRole.Admin, IsActive = true, IsBreakGlass = true,
            },
            new User
            {
                Id = Guid.NewGuid(), Username = "ordinary", Provider = AuthProvider.Local,
                PasswordHash = passwordHash, Role = UserRole.Operator, IsActive = true, IsBreakGlass = false,
            });
        await db.SaveChangesAsync();
        var config = CreateConfig();
        var active = new ActiveAuthenticationConfiguration(
            LocalLoginMode.BreakGlassOnly, false, false, false, "Single Sign-On");

        AuthController Controller() => new(
            db, config, NoopAuditWriter.Instance, new TestJwtKeyProvider(),
            new AuthSessionIssuer(config, new TestJwtKeyProvider(), NoopAuditWriter.Instance),
            activeAuthentication: active)
        {
            ControllerContext = new ControllerContext { HttpContext = HttpCtx(tokenInBody: true) },
        };

        (await Controller().Login(new LoginRequest("recovery", "password123"), default)).Result
            .Should().BeOfType<OkObjectResult>();
        (await Controller().Login(new LoginRequest("ordinary", "password123"), default)).Result
            .Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task Login_ValidCredentials_ReturnsToken()
    {
        // Arrange
        var db = CreateContext();
        var config = CreateConfig();
        var passwordHash = BCrypt.Net.BCrypt.HashPassword("correctpassword");
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            PasswordHash = passwordHash,
            Role = UserRole.Operator
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var controller = new AuthController(db, config, NoopAuditWriter.Instance, new TestJwtKeyProvider(), new NodePilot.Api.Security.AuthSessionIssuer(config ?? CreateConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance))
        {
            // Programmatic caller (CLI-style) — opts in to the JWT in the body.
            ControllerContext = new ControllerContext { HttpContext = HttpCtx(tokenInBody: true) },
        };
        var request = new LoginRequest("testuser", "correctpassword");

        // Act
        var result = await controller.Login(request, CancellationToken.None);

        // Assert
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<LoginResponse>().Subject;
        response.Token.Should().NotBeNullOrEmpty();
        response.Username.Should().Be("testuser");
        response.Role.Should().Be("Operator");
    }

    [Fact]
    public async Task Login_BrowserCaller_NoOptInHeader_OmitsTokenFromBody()
    {
        // Security-audit finding H-5 completion: a browser login (no X-Auth-Token-Response header) must receive
        // identity only. The JWT reaches the browser solely via the httpOnly np_auth cookie,
        // so a future XSS has no response body to read a portable bearer token out of.
        var db = CreateContext();
        var config = CreateConfig();
        db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("correctpassword"),
            Role = UserRole.Operator,
        });
        await db.SaveChangesAsync();

        var controller = new AuthController(db, config, NoopAuditWriter.Instance, new TestJwtKeyProvider(), new NodePilot.Api.Security.AuthSessionIssuer(config ?? CreateConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance))
        {
            ControllerContext = new ControllerContext { HttpContext = HttpCtx() },
        };

        var result = await controller.Login(new LoginRequest("testuser", "correctpassword"), CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var identity = ok.Value.Should().BeOfType<AuthIdentityResponse>().Subject;
        identity.Username.Should().Be("testuser");
        identity.Role.Should().Be("Operator");
        // The JWT must still be set as the httpOnly cookie, just not in the JSON body.
        controller.Response.Headers.SetCookie.ToString().Should().Contain(AuthController.AuthCookieName);
    }

    [Fact]
    public async Task Login_InvalidPassword_ReturnsUnauthorized()
    {
        // Arrange
        var db = CreateContext();
        var config = CreateConfig();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("correctpassword"),
            Role = UserRole.Operator
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var controller = new AuthController(db, config, NoopAuditWriter.Instance, new TestJwtKeyProvider(), new NodePilot.Api.Security.AuthSessionIssuer(config ?? CreateConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance));
        var request = new LoginRequest("testuser", "wrongpassword");

        // Act
        var result = await controller.Login(request, CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task Login_UnknownUser_WhenUsersExist_ReturnsUnauthorized()
    {
        // Arrange
        var db = CreateContext();
        var config = CreateConfig();
        var existingUser = new User
        {
            Id = Guid.NewGuid(),
            Username = "existing",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("password"),
            Role = UserRole.Admin
        };
        db.Users.Add(existingUser);
        await db.SaveChangesAsync();

        var controller = new AuthController(db, config, NoopAuditWriter.Instance, new TestJwtKeyProvider(), new NodePilot.Api.Security.AuthSessionIssuer(config ?? CreateConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance));
        var request = new LoginRequest("unknown", "password");

        // Act
        var result = await controller.Login(request, CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task GetMe_Authenticated_ReturnsUserInfo()
    {
        // Arrange
        var db = CreateContext();
        var config = CreateConfig();
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Username = "admin",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("password"),
            Role = UserRole.Admin
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var controller = new AuthController(db, config, NoopAuditWriter.Instance, new TestJwtKeyProvider(), new NodePilot.Api.Security.AuthSessionIssuer(config ?? CreateConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim(ClaimTypes.Name, "admin"),
                    new Claim(ClaimTypes.Role, "Admin")
                }, "test"))
            }
        };

        // Act
        var result = await controller.GetCurrentUser(CancellationToken.None);

        // Assert
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task GetMe_Unauthenticated_ReturnsUnauthorized()
    {
        // Arrange
        var db = CreateContext();
        var config = CreateConfig();
        var controller = new AuthController(db, config, NoopAuditWriter.Instance, new TestJwtKeyProvider(), new NodePilot.Api.Security.AuthSessionIssuer(config ?? CreateConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity())
            }
        };

        // Act
        var result = await controller.GetCurrentUser(CancellationToken.None);

        // Assert
        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task Refresh_WithValidUser_ReturnsNewToken()
    {
        var db = CreateContext();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "admin",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("secret"),
            Role = UserRole.Admin,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var controller = new AuthController(db, CreateConfig(), NoopAuditWriter.Instance, new TestJwtKeyProvider(), new NodePilot.Api.Security.AuthSessionIssuer(CreateConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                    new Claim(ClaimTypes.Name, user.Username),
                    new Claim(ClaimTypes.Role, user.Role.ToString()),
                }, "test")),
            },
        };
        // Bearer-header caller (CLI/API) → the rotated token IS returned in the body.
        controller.Request.Headers.Authorization = "Bearer dummy.presented.token";

        var result = await controller.Refresh(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<LoginResponse>().Subject;
        response.Token.Should().NotBeNullOrEmpty();
        response.Username.Should().Be("admin");
        response.Role.Should().Be("Admin");
    }

    [Fact]
    public async Task Refresh_CookieAuth_OmitsTokenFromBody_ButRotatesCookie()
    {
        // The XSS-relevant path: a browser refreshes via the np_auth cookie (no Bearer header).
        // The body must carry identity only; the rotated JWT reaches the browser solely through
        // the refreshed httpOnly cookie.
        var db = CreateContext();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "admin",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("secret"),
            Role = UserRole.Admin,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var controller = new AuthController(db, CreateConfig(), NoopAuditWriter.Instance, new TestJwtKeyProvider(), new NodePilot.Api.Security.AuthSessionIssuer(CreateConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                    new Claim(ClaimTypes.Name, user.Username),
                    new Claim(ClaimTypes.Role, user.Role.ToString()),
                }, "test")),
            },
        };
        // No Authorization header — this is the cookie-authenticated browser case.

        var result = await controller.Refresh(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<AuthIdentityResponse>("a cookie-auth browser must not get the JWT in the body");
        controller.Response.Headers.SetCookie.ToString().Should().Contain(AuthController.AuthCookieName,
            "the rotated token must still be delivered as the httpOnly cookie");
    }

    [Fact]
    public async Task Refresh_CookieAuthWithOptInHeader_StillOmitsToken()
    {
        // Defence against an XSS trying to opt in: refresh deliberately does NOT honour the
        // X-Auth-Token-Response header. Authentication is via the cookie (no Bearer header),
        // so the response stays identity-only even though the opt-in header is present.
        var db = CreateContext();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "admin",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("secret"),
            Role = UserRole.Admin,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var controller = new AuthController(db, CreateConfig(), NoopAuditWriter.Instance, new TestJwtKeyProvider(), new NodePilot.Api.Security.AuthSessionIssuer(CreateConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                    new Claim(ClaimTypes.Name, user.Username),
                    new Claim(ClaimTypes.Role, user.Role.ToString()),
                }, "test")),
            },
        };
        // The forged opt-in header an XSS could set — must be ignored on the refresh path.
        controller.Request.Headers[AuthController.TokenResponseHeader] = "true";

        var result = await controller.Refresh(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<AuthIdentityResponse>(
            "the opt-in header must NOT leak a token on the cookie-authenticated refresh path");
    }

    [Fact]
    public async Task Refresh_DeletedUser_ReturnsUnauthorized()
    {
        var db = CreateContext();
        var ghostId = Guid.NewGuid();
        // no user row inserted â€” simulates a token from a now-deleted account

        var controller = new AuthController(db, CreateConfig(), NoopAuditWriter.Instance, new TestJwtKeyProvider(), new NodePilot.Api.Security.AuthSessionIssuer(CreateConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, ghostId.ToString()),
                }, "test")),
            },
        };

        var result = await controller.Refresh(CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task Refresh_NoClaims_ReturnsUnauthorized()
    {
        var db = CreateContext();
        var controller = new AuthController(db, CreateConfig(), NoopAuditWriter.Instance, new TestJwtKeyProvider(), new NodePilot.Api.Security.AuthSessionIssuer(CreateConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) },
        };

        var result = await controller.Refresh(CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedResult>();
    }

    /// <summary>
    /// Token rotation must leave a forensic trail. Without this audit row, a stolen JWT
    /// being renewed every 11h would be invisible — the AuthTokenRevocations metric tracks
    /// the count but not the actor. Distinct action code from LOGIN_SUCCESS so SIEM
    /// dashboards that count active logins are not double-counted by 12h refresh cadence.
    /// </summary>
    [Fact]
    public async Task Refresh_EmitsTokenRefreshedAudit()
    {
        var db = CreateContext();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("secret"),
            Role = UserRole.Operator,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var audit = new CapturingAuditWriter();
        var controller = new AuthController(db, CreateConfig(), audit, new TestJwtKeyProvider(),
            new NodePilot.Api.Security.AuthSessionIssuer(CreateConfig(), new TestJwtKeyProvider(), audit));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                    new Claim(ClaimTypes.Name, user.Username),
                    new Claim(ClaimTypes.Role, user.Role.ToString()),
                }, "test")),
            },
        };

        await controller.Refresh(CancellationToken.None);

        var entry = audit.Calls.Should().ContainSingle(c => c.Action == "TOKEN_REFRESHED").Subject;
        entry.ResourceType.Should().Be("User");
        entry.ResourceId.Should().Be(user.Id);
        entry.Details.Should().Contain("alice").And.Contain("Operator");
    }
}
