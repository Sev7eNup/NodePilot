using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NodePilot.Api.Security;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Security;

public class TokenValidityMiddlewareTests
{
    private static HttpContext MakeContext(
        bool authenticated = true,
        string? jti = null,
        Guid? userId = null,
        long? iatSeconds = null,
        long? iatMs = null,
        int? secStamp = null,
        UserRole? role = null,
        Guid? sessionId = null,
        NodePilot.Data.NodePilotDbContext? db = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new System.IO.MemoryStream();

        if (!authenticated) return ctx;

        if (db is not null && userId is not null)
        {
            jti ??= Guid.NewGuid().ToString("N");
            sessionId ??= Guid.NewGuid();
            secStamp ??= db.Users.Local.FirstOrDefault(x => x.Id == userId)?.SecurityStamp
                         ?? db.Users.AsNoTracking().Where(x => x.Id == userId)
                             .Select(x => x.SecurityStamp).Single();
            role ??= db.Users.Local.FirstOrDefault(x => x.Id == userId)?.Role
                     ?? db.Users.AsNoTracking().Where(x => x.Id == userId)
                         .Select(x => x.Role).Single();
            db.AuthSessions.Add(new AuthSession
            {
                Id = sessionId.Value,
                UserId = userId.Value,
                AuthenticationMethod = "Test",
                CurrentJti = jti,
                AuthorizationVersion = secStamp.Value,
                CreatedAt = DateTime.UtcNow,
                LastSeenAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(1),
            });
            db.SaveChanges();
        }

        var claims = new List<Claim>();
        if (jti is not null)
            claims.Add(new Claim(JwtRegisteredClaimNames.Jti, jti));
        if (userId is not null)
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()));
        if (iatSeconds is not null)
            claims.Add(new Claim(JwtRegisteredClaimNames.Iat, iatSeconds.Value.ToString()));
        if (iatMs is not null)
            claims.Add(new Claim("np_iat_ms", iatMs.Value.ToString()));
        if (secStamp is not null)
            claims.Add(new Claim("np_secstamp", secStamp.Value.ToString()));
        if (role is not null)
            claims.Add(new Claim(ClaimTypes.Role, role.Value.ToString()));
        if (sessionId is not null)
            claims.Add(new Claim(AuthSessionIssuer.SessionIdClaim, sessionId.Value.ToString()));

        var identity = new ClaimsIdentity(claims, "TestAuth");
        ctx.User = new ClaimsPrincipal(identity);
        return ctx;
    }

    private static User MakeUser(Guid? id = null, bool isActive = true, DateTime? passwordChangedAt = null,
        int securityStamp = 0)
        => new()
        {
            Id = id ?? Guid.NewGuid(),
            Username = "testuser",
            PasswordHash = "hash",
            IsActive = isActive,
            PasswordChangedAt = passwordChangedAt ?? DateTime.UtcNow.AddDays(-1),
            SecurityStamp = securityStamp
        };

    // Fresh cache per test: the middleware caches revocation/user-state lookups for 30 s
    // and sharing a static instance between tests would leak state across them.
    private static IMemoryCache NewCache() => new MemoryCache(new MemoryCacheOptions());

    [Fact]
    public async Task Invoke_ValidToken_CallsNext()
    {
        var db = TestDbFactory.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(MakeUser(userId, isActive: true));
        await db.SaveChangesAsync();

        var jti = Guid.NewGuid().ToString();
        var ctx = MakeContext(jti: jti, userId: userId, db: db);
        var nextCalled = false;
        var middleware = new TokenValidityMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var cache = NewCache();

        await middleware.Invoke(ctx, db, cache);

        nextCalled.Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task Invoke_RevokedJti_Returns401_DoesNotCallNext()
    {
        var db = TestDbFactory.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(MakeUser(userId));
        var jti = Guid.NewGuid().ToString();
        db.RevokedTokens.Add(new RevokedToken
        {
            Jti = jti,
            UserId = userId,
            RevokedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(12)
        });
        await db.SaveChangesAsync();

        var ctx = MakeContext(jti: jti, userId: userId, db: db);
        var nextCalled = false;
        var middleware = new TokenValidityMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var cache = NewCache();

        await middleware.Invoke(ctx, db, cache);

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task Invoke_AllowAnonymousWithRevokedCookie_ContinuesAsAnonymous()
    {
        var db = TestDbFactory.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(MakeUser(userId));
        var jti = Guid.NewGuid().ToString();
        db.RevokedTokens.Add(new RevokedToken
        {
            Jti = jti, UserId = userId, RevokedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
        });
        await db.SaveChangesAsync();
        var ctx = MakeContext(jti: jti, userId: userId, db: db);
        ctx.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new AllowAnonymousAttribute()),
            "anonymous-test"));
        var nextCalled = false;
        var middleware = new TokenValidityMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.Invoke(ctx, db, NewCache());

        nextCalled.Should().BeTrue();
        ctx.User.Identity!.IsAuthenticated.Should().BeFalse();
        ctx.Items[TokenValidityMiddleware.InvalidatedPrincipalItem]
            .Should().BeOfType<ClaimsPrincipal>()
            .Which.FindFirstValue(JwtRegisteredClaimNames.Jti).Should().Be(jti);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Invoke_AllowAnonymousWithValidCookie_PreservesAuthenticatedPrincipal()
    {
        var db = TestDbFactory.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(MakeUser(userId));
        await db.SaveChangesAsync();
        var ctx = MakeContext(jti: Guid.NewGuid().ToString(), userId: userId, db: db);
        ctx.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new AllowAnonymousAttribute()),
            "anonymous-test"));
        var middleware = new TokenValidityMiddleware(_ => Task.CompletedTask);

        await middleware.Invoke(ctx, db, NewCache());

        ctx.User.Identity!.IsAuthenticated.Should().BeTrue();
    }

    [Fact]
    public async Task Invoke_DoesNotNegativeCacheUnrevokedJti()
    {
        var db = TestDbFactory.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(MakeUser(userId));
        await db.SaveChangesAsync();

        var jti = Guid.NewGuid().ToString();
        var middleware = new TokenValidityMiddleware(_ => Task.CompletedTask);
        var cache = NewCache();

        await middleware.Invoke(MakeContext(jti: jti, userId: userId, db: db), db, cache);

        db.RevokedTokens.Add(new RevokedToken
        {
            Jti = jti,
            UserId = userId,
            RevokedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(12)
        });
        await db.SaveChangesAsync();

        var second = MakeContext(jti: jti, userId: userId, db: db);
        await middleware.Invoke(second, db, cache);

        second.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task Invoke_DeactivatedUser_Returns401()
    {
        var db = TestDbFactory.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(MakeUser(userId, isActive: false));
        await db.SaveChangesAsync();

        var jti = Guid.NewGuid().ToString();
        var ctx = MakeContext(jti: jti, userId: userId, db: db);
        var nextCalled = false;
        var middleware = new TokenValidityMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var cache = NewCache();

        await middleware.Invoke(ctx, db, cache);

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task Invoke_NoJtiClaim_Returns401()
    {
        // No jti claim → jti revocation is skipped, but user check still runs
        var db = TestDbFactory.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(MakeUser(userId, isActive: true));
        await db.SaveChangesAsync();

        // no jti passed
        var ctx = MakeContext(authenticated: true, jti: null, userId: userId);
        var nextCalled = false;
        var middleware = new TokenValidityMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var cache = NewCache();

        await middleware.Invoke(ctx, db, cache);

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Invoke_Unauthenticated_CallsNext()
    {
        var db = TestDbFactory.Create();
        var ctx = MakeContext(authenticated: false);
        var nextCalled = false;
        var middleware = new TokenValidityMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var cache = NewCache();

        await middleware.Invoke(ctx, db, cache);

        nextCalled.Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task Invoke_TokenIssuedBeforePasswordChange_Returns401()
    {
        var db = TestDbFactory.Create();
        var userId = Guid.NewGuid();
        var passwordChangedAt = DateTime.UtcNow.AddMinutes(-5);
        db.Users.Add(MakeUser(userId, isActive: true, passwordChangedAt: passwordChangedAt));
        await db.SaveChangesAsync();

        // Token issued 10 minutes before password change → stale
        var tokenIssuedAt = passwordChangedAt.AddMinutes(-10);
        var iatMs = new DateTimeOffset(tokenIssuedAt).ToUnixTimeMilliseconds();
        var ctx = MakeContext(jti: Guid.NewGuid().ToString(), userId: userId, iatMs: iatMs, db: db);
        var nextCalled = false;
        var middleware = new TokenValidityMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var cache = NewCache();

        await middleware.Invoke(ctx, db, cache);

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task Invoke_TokenIssuedAfterPasswordChange_CallsNext()
    {
        var db = TestDbFactory.Create();
        var userId = Guid.NewGuid();
        var passwordChangedAt = DateTime.UtcNow.AddMinutes(-10);
        db.Users.Add(MakeUser(userId, isActive: true, passwordChangedAt: passwordChangedAt));
        await db.SaveChangesAsync();

        // Token issued 5 minutes after password change → valid
        var tokenIssuedAt = passwordChangedAt.AddMinutes(5);
        var iatMs = new DateTimeOffset(tokenIssuedAt).ToUnixTimeMilliseconds();
        var ctx = MakeContext(jti: Guid.NewGuid().ToString(), userId: userId, iatMs: iatMs, db: db);
        var nextCalled = false;
        var middleware = new TokenValidityMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var cache = NewCache();

        await middleware.Invoke(ctx, db, cache);

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Invoke_RevokedAndDeactivated_Returns401()
    {
        var db = TestDbFactory.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(MakeUser(userId, isActive: false));
        var jti = Guid.NewGuid().ToString();
        db.RevokedTokens.Add(new RevokedToken
        {
            Jti = jti,
            UserId = userId,
            RevokedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        await db.SaveChangesAsync();

        var ctx = MakeContext(jti: jti, userId: userId, db: db);
        var nextCalled = false;
        var middleware = new TokenValidityMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var cache = NewCache();

        await middleware.Invoke(ctx, db, cache);

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task Invoke_StampMismatch_Returns401()
    {
        // H-1 (a finding from the security audit): SecurityStamp on the row is ahead of
        // the token's np_secstamp claim. Models the "demoted-but-token-still-alive" scenario:
        // an admin demoted to Viewer holds a token minted when their stamp was N-1; the
        // row has been bumped to N; the middleware must reject the stale token.
        var db = TestDbFactory.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(MakeUser(userId, isActive: true, securityStamp: 5));
        await db.SaveChangesAsync();

        var ctx = MakeContext(jti: Guid.NewGuid().ToString(), userId: userId, secStamp: 4, db: db);
        var nextCalled = false;
        var middleware = new TokenValidityMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var cache = NewCache();

        await middleware.Invoke(ctx, db, cache);

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task Invoke_StampMatch_CallsNext()
    {
        // Counter-test: matching stamp must let the request through.
        var db = TestDbFactory.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(MakeUser(userId, isActive: true, securityStamp: 7));
        await db.SaveChangesAsync();

        var ctx = MakeContext(jti: Guid.NewGuid().ToString(), userId: userId, secStamp: 7, db: db);
        var nextCalled = false;
        var middleware = new TokenValidityMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var cache = NewCache();

        await middleware.Invoke(ctx, db, cache);

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Invoke_ForgedRoleWithValidSessionAndStamp_Returns401()
    {
        var db = TestDbFactory.Create();
        var user = MakeUser(securityStamp: 7);
        user.Role = UserRole.Viewer;
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var ctx = MakeContext(
            jti: Guid.NewGuid().ToString(),
            userId: user.Id,
            secStamp: user.SecurityStamp,
            role: UserRole.Admin,
            db: db);
        var nextCalled = false;

        await new TokenValidityMiddleware(_ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            })
            .Invoke(ctx, db, NewCache());

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Invoke_AdditionalForgedAdminRole_Returns401()
    {
        var db = TestDbFactory.Create();
        var user = MakeUser(securityStamp: 3);
        user.Role = UserRole.Viewer;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var ctx = MakeContext(userId: user.Id, role: UserRole.Viewer, db: db);
        ((ClaimsIdentity)ctx.User.Identity!).AddClaim(
            new Claim(ClaimTypes.Role, UserRole.Admin.ToString()));
        var nextCalled = false;

        await new TokenValidityMiddleware(_ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            })
            .Invoke(ctx, db, NewCache());

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Invoke_NoStampClaim_Returns401()
    {
        // Tokens minted before the H-1 fix (and the existing test fixtures that mint
        // without a stamp claim) are parsed as stamp=0 and only succeed against a row whose
        // SecurityStamp is also 0 — which is the default value the migration used for the
        // new column, so existing accounts on a freshly-migrated DB stay logged in until
        // the first stamp bump.
        var db = TestDbFactory.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(MakeUser(userId, isActive: true, securityStamp: 0));
        await db.SaveChangesAsync();

        var ctx = MakeContext(jti: Guid.NewGuid().ToString(), userId: userId); // no secStamp
        var nextCalled = false;
        var middleware = new TokenValidityMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var cache = NewCache();

        await middleware.Invoke(ctx, db, cache);

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Invoke_IatSecondsPrecision_AlsoChecked()
    {
        // Uses standard iat (seconds) fallback when np_iat_ms is absent
        var db = TestDbFactory.Create();
        var userId = Guid.NewGuid();
        var passwordChangedAt = DateTime.UtcNow.AddMinutes(-5);
        db.Users.Add(MakeUser(userId, isActive: true, passwordChangedAt: passwordChangedAt));
        await db.SaveChangesAsync();

        var tokenIssuedAt = passwordChangedAt.AddMinutes(-2);
        var iatSec = new DateTimeOffset(tokenIssuedAt).ToUnixTimeSeconds();
        var ctx = MakeContext(jti: Guid.NewGuid().ToString(), userId: userId, iatSeconds: iatSec, db: db);
        var nextCalled = false;
        var middleware = new TokenValidityMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var cache = NewCache();

        await middleware.Invoke(ctx, db, cache);

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task Invoke_StaleExternalAuthorizationSnapshot_Returns401()
    {
        var db = TestDbFactory.Create();
        var user = MakeUser();
        user.Provider = AuthProvider.Ldap;
        user.LastDirectorySyncAt = DateTime.UtcNow.AddMinutes(-16);
        user.DirectorySyncStatus = "Stale";
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var ctx = MakeContext(userId: user.Id, secStamp: user.SecurityStamp, db: db);
        var nextCalled = false;

        await new TokenValidityMiddleware(_ => { nextCalled = true; return Task.CompletedTask; })
            .Invoke(ctx, db, NewCache());

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Invoke_RevokedServerSideSession_Returns401()
    {
        var db = TestDbFactory.Create();
        var user = MakeUser(securityStamp: 4);
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            AuthenticationMethod = "Ldap",
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
            LastSeenAt = DateTime.UtcNow.AddMinutes(-1),
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            RevokedAt = DateTime.UtcNow,
            AuthorizationVersion = 4,
            CurrentJti = "revoked-session-token",
        };
        db.Users.Add(user);
        db.AuthSessions.Add(session);
        await db.SaveChangesAsync();
        var ctx = MakeContext(
            jti: session.CurrentJti,
            userId: user.Id,
            secStamp: user.SecurityStamp,
            sessionId: session.Id);
        var nextCalled = false;

        await new TokenValidityMiddleware(_ => { nextCalled = true; return Task.CompletedTask; })
            .Invoke(ctx, db, NewCache());

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }
}
