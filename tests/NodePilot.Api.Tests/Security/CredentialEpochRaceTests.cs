using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.Hosting;
using NodePilot.Api.Security;
using NodePilot.Api.Tests.TestSupport;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Security;

public sealed class CredentialEpochRaceTests
{
    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = "NodePilot", ["Jwt:Audience"] = "NodePilot",
            ["Authentication:LocalLoginMode"] = "Enabled",
        }).Build();

    private static User NewUser() => new()
    {
        Id = Guid.NewGuid(), Username = "epoch-user", IsActive = true,
        Role = UserRole.Operator, PasswordHash = BCrypt.Net.BCrypt.HashPassword("original-password", 4),
        PasswordChangedAt = DateTime.UtcNow.AddDays(-1), SecurityStamp = 3,
    };

    private static AuthController Controller(NodePilotDbContext db, HttpContext context) => new(
        db, Config(), NoopAuditWriter.Instance,
        new AuthSessionIssuer(Config(), new TestJwtKeyProvider(), NoopAuditWriter.Instance, db: db))
        { ControllerContext = new ControllerContext { HttpContext = context } };

    private static ClaimsPrincipal ValidateSignature(string token) => new JwtSecurityTokenHandler().ValidateToken(
        token, new TokenValidationParameters
        {
            ValidIssuer = "NodePilot", ValidAudience = "NodePilot",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtKeyProvider.DefaultKey)),
            ValidateIssuerSigningKey = true, ValidateLifetime = true,
        }, out _);

    private static async Task<bool> IsAcceptedAsync(NodePilotDbContext db, string token)
    {
        var accepted = false;
        var context = new DefaultHttpContext { User = ValidateSignature(token) };
        context.Request.Path = "/api/workflows";
        context.Response.Body = new MemoryStream();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        await new TokenValidityMiddleware(_ =>
        {
            accepted = true;
            return Task.CompletedTask;
        }, new DatabaseAvailabilityOptions()).Invoke(context, db, cache);
        return accepted;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Login_DoesNotAdoptPasswordEpochChangedAfterVerification(bool resetDuringLogin)
    {
        await using var db = TestDbFactory.Create();
        db.Users.Add(NewUser());
        await db.SaveChangesAsync();
        if (resetDuringLogin)
        {
            // The successful-login counter reset runs after BCrypt verification and before
            // the controller reloads the user. This trigger deterministically interleaves
            // the same password/stamp change as an administrator reset at that boundary.
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER reset_password_after_verification
                AFTER UPDATE OF FailedLoginCount ON Users
                WHEN NEW.FailedLoginCount = 0 AND OLD.FailedLoginCount > 0
                BEGIN
                    UPDATE Users SET PasswordHash = 'replacement-password-hash',
                        SecurityStamp = SecurityStamp + 1, PasswordChangedAt = CURRENT_TIMESTAMP
                    WHERE Id = NEW.Id;
                END;
                """);
        }
        var context = new DefaultHttpContext();
        context.Request.Headers[AuthController.TokenResponseHeader] = "true";

        var result = await Controller(db, context).Login(new LoginRequest("epoch-user", "original-password"), default);

        var response = (result.Result as OkObjectResult)?.Value as LoginResponse;
        var accepted = response is not null && await IsAcceptedAsync(db, response.Token);
        accepted.Should().Be(!resetDuringLogin,
            "the only password proved belongs to the original authorization epoch");
        if (resetDuringLogin)
        {
            result.Result.Should().BeOfType<UnauthorizedObjectResult>();
            (await db.AuthSessions.CountAsync()).Should().Be(0);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_DoesNotAdoptPasswordEpochChangedAfterMiddleware(bool resetDuringRefresh)
    {
        await using var db = TestDbFactory.Create();
        var user = NewUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var original = await new AuthSessionIssuer(Config(), new TestJwtKeyProvider(),
            NoopAuditWriter.Instance, db: db).IssueAsync(user, AuthSource.Local, new DefaultHttpContext(), default);
        var context = new DefaultHttpContext { User = ValidateSignature(original.Token) };
        context.Request.Path = "/api/auth/refresh";
        context.Request.Headers.Authorization = "Bearer " + original.Token;
        context.Response.Body = new MemoryStream();
        ActionResult<LoginResponse>? result = null;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        await new TokenValidityMiddleware(async ctx =>
        {
            if (resetDuringRefresh)
                await db.Users.ExecuteUpdateAsync(setters => setters
                    .SetProperty(u => u.SecurityStamp, u => u.SecurityStamp + 1)
                    .SetProperty(u => u.PasswordHash, "replacement-password-hash")
                    .SetProperty(u => u.PasswordChangedAt, DateTime.UtcNow));
            db.ChangeTracker.Clear();
            result = await Controller(db, ctx).Refresh(default);
        }, new DatabaseAvailabilityOptions()).Invoke(context, db, cache);

        result.Should().NotBeNull("the original token must pass middleware before the reset");
        var response = (result?.Result as OkObjectResult)?.Value as LoginResponse;
        var accepted = response is not null && await IsAcceptedAsync(db, response.Token);
        accepted.Should().Be(!resetDuringRefresh,
            "refresh must prove the presented session epoch, never adopt a later password reset");
        if (resetDuringRefresh)
        {
            result?.Result.Should().BeOfType<UnauthorizedObjectResult>();
            (await db.AuthSessions.AsNoTracking().SingleAsync()).AuthorizationVersion.Should().Be(3);
        }
    }
}
