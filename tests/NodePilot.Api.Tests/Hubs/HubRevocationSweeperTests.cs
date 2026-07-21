using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NodePilot.Api.Hubs;
using NodePilot.Api.Security;
using NodePilot.Api.Security.Oidc;
using NodePilot.Core.Models;
using NodePilot.Data;
using Xunit;

namespace NodePilot.Api.Tests.Hubs;

/// <summary>
/// M2/M-3 (security-audit findings) regression coverage. The sweeper must:
///   * read the union of (revoked-and-not-yet-expired tokens, deactivated users) from DB
///   * call FindConnectionsToDrop for live connections matching either set
///   * send a "forceDisconnect" client event
///   * call HubCallerContext.Abort() on the server side
///   * remove the connection from the auth map afterward (ForgetConnection)
///
/// Tests use the internal <c>SweepOnceAsync</c> hook to skip the 10s warm-up delay of
/// the BackgroundService loop.
/// </summary>
[Collection(ExecutionHubStaticStateCollection.Name)]
public sealed class HubRevocationSweeperTests : IDisposable
{
    public HubRevocationSweeperTests() => ExecutionHub.ClearAuthMapForTest();
    public void Dispose() => ExecutionHub.ClearAuthMapForTest();

    private static IServiceScopeFactory ScopeFactoryFor(NodePilotDbContext db)
    {
        // Wrap the test DbContext in a one-off scope factory so SweepOnceAsync's
        // CreateScope().GetRequiredService<NodePilotDbContext>() returns the seeded ctx.
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(Options.Create(new AuthenticationPolicyOptions()));
        services.AddSingleton(Options.Create(new EnterpriseOidcOptions()));
        services.AddScoped<ExternalAuthorizationEvaluator>();
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static (HubRevocationSweeper sweeper, Mock<IHubContext<ExecutionHub>> hub, Mock<ISingleClientProxy> proxy, Mock<IHubClients> clients) Build(NodePilotDbContext db)
    {
        // Note: IHubClients.Client(connId) returns ISingleClientProxy (newer SignalR API),
        // not the legacy IClientProxy. Mock the right surface or the .Returns chain doesn't
        // type-check against the production code.
        var proxy = new Mock<ISingleClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);

        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Client(It.IsAny<string>())).Returns(proxy.Object);

        var hub = new Mock<IHubContext<ExecutionHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);

        var sweeper = new HubRevocationSweeper(
            ScopeFactoryFor(db),
            hub.Object,
            NullLogger<HubRevocationSweeper>.Instance,
            new ConfigurationBuilder().Build());

        return (sweeper, hub, proxy, clients);
    }

    [Fact]
    public async Task SweepOnceAsync_NoRevokedOrDeactivated_DoesNothing()
    {
        var db = NodePilot.TestCommons.TestDbFactory.Create();
        var ctx = Mock.Of<HubCallerContext>();
        ExecutionHub.RegisterAuthForTest("conn-1", "j-1", Guid.NewGuid(), ctx);

        var (sweeper, _, proxy, _) = Build(db);

        await sweeper.SweepOnceAsync(CancellationToken.None);

        proxy.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Mock.Get(ctx).Verify(c => c.Abort(), Times.Never);
    }

    [Fact]
    public async Task SweepOnceAsync_RevokedTokenStillFresh_AbortsConnection()
    {
        var db = NodePilot.TestCommons.TestDbFactory.Create();
        var jti = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        db.RevokedTokens.Add(new RevokedToken
        {
            Jti = jti,
            UserId = userId,
            RevokedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1), // not yet expired
        });
        await db.SaveChangesAsync();

        var ctx = new Mock<HubCallerContext>();
        ExecutionHub.RegisterAuthForTest("conn-1", jti, userId, ctx.Object);

        var (sweeper, _, proxy, _) = Build(db);

        await sweeper.SweepOnceAsync(CancellationToken.None);

        // Two-step termination: forceDisconnect notify + Abort().
        proxy.Verify(p => p.SendCoreAsync(
            "forceDisconnect",
            It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
        ctx.Verify(c => c.Abort(), Times.Once);
        // Auth-map entry must be cleared so a second sweep doesn't reprocess it.
        ExecutionHub.TryGetContext("conn-1").Should().BeNull();
    }

    [Fact]
    public async Task SweepOnceAsync_AlreadyExpiredToken_NotLoaded_NoAbort()
    {
        // Performance contract: tokens whose ExpiresAt is in the past fail JwtBearer
        // validation on handshake anyway, so the sweeper deliberately filters them out
        // of the working set. Pin it.
        var db = NodePilot.TestCommons.TestDbFactory.Create();
        var jti = Guid.NewGuid().ToString();
        db.RevokedTokens.Add(new RevokedToken
        {
            Jti = jti,
            UserId = Guid.NewGuid(),
            RevokedAt = DateTime.UtcNow.AddHours(-2),
            ExpiresAt = DateTime.UtcNow.AddMinutes(-5), // already expired
        });
        await db.SaveChangesAsync();

        var ctx = new Mock<HubCallerContext>();
        ExecutionHub.RegisterAuthForTest("conn-1", jti, Guid.NewGuid(), ctx.Object);

        var (sweeper, _, _, _) = Build(db);

        await sweeper.SweepOnceAsync(CancellationToken.None);

        ctx.Verify(c => c.Abort(), Times.Never,
            "expired tokens are filtered out — they fail handshake validation anyway");
    }

    [Fact]
    public async Task SweepOnceAsync_DeactivatedUser_AbortsAllConnections()
    {
        var db = NodePilot.TestCommons.TestDbFactory.Create();
        var deactivated = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = deactivated,
            Username = "exiled",
            PasswordHash = "x",
            IsActive = false,
        });
        await db.SaveChangesAsync();

        var ctx1 = new Mock<HubCallerContext>();
        var ctx2 = new Mock<HubCallerContext>();
        ExecutionHub.RegisterAuthForTest("conn-a", "j1", deactivated, ctx1.Object);
        ExecutionHub.RegisterAuthForTest("conn-b", "j2", deactivated, ctx2.Object);
        ExecutionHub.RegisterAuthForTest("conn-c", "j3", Guid.NewGuid(), Mock.Of<HubCallerContext>());

        var (sweeper, _, _, _) = Build(db);

        await sweeper.SweepOnceAsync(CancellationToken.None);

        // Both connections of the deactivated user must be aborted; the unrelated one
        // must stay alive.
        ctx1.Verify(c => c.Abort(), Times.Once);
        ctx2.Verify(c => c.Abort(), Times.Once);
        ExecutionHub.TryGetContext("conn-c").Should().NotBeNull(
            "connections of active users must NOT be aborted");
    }

    [Fact]
    public async Task SweepOnceAsync_SecurityStampChanged_AbortsStaleConnection()
    {
        var db = NodePilot.TestCommons.TestDbFactory.Create();
        var user = new User
        {
            Id = Guid.NewGuid(), Username = "demoted", PasswordHash = "x",
            IsActive = true, SecurityStamp = 8,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var stale = new Mock<HubCallerContext>();
        var current = new Mock<HubCallerContext>();
        ExecutionHub.RegisterAuthForTest("conn-stale", "j1", user.Id, stale.Object, securityStamp: 7);
        ExecutionHub.RegisterAuthForTest("conn-current", "j2", user.Id, current.Object, securityStamp: 8);

        var (sweeper, _, _, _) = Build(db);
        await sweeper.SweepOnceAsync(CancellationToken.None);

        stale.Verify(c => c.Abort(), Times.Once);
        current.Verify(c => c.Abort(), Times.Never);
    }

    [Fact]
    public async Task SweepOnceAsync_ExpiredServerSessionWithoutRevokedToken_AbortsConnection()
    {
        var db = NodePilot.TestCommons.TestDbFactory.Create();
        var user = new User
        {
            Id = Guid.NewGuid(), Username = "expired-session", PasswordHash = "x",
            IsActive = true,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(), UserId = user.Id, AuthenticationMethod = "Local",
            CurrentJti = "session-jti", CreatedAt = DateTime.UtcNow.AddHours(-9),
            LastSeenAt = DateTime.UtcNow.AddHours(-1), ExpiresAt = DateTime.UtcNow.AddSeconds(-1),
        };
        db.AddRange(user, session);
        await db.SaveChangesAsync();
        var context = new Mock<HubCallerContext>();
        ExecutionHub.RegisterAuthForTest(
            "conn-expired", session.CurrentJti, user.Id, context.Object,
            sessionId: session.Id, tokenExpiresAt: DateTime.UtcNow.AddHours(1));

        var (sweeper, _, _, _) = Build(db);
        await sweeper.SweepOnceAsync(CancellationToken.None);

        context.Verify(c => c.Abort(), Times.Once);
    }

    [Fact]
    public async Task SweepOnceAsync_ForceDisconnectNotifyFails_StillAborts()
    {
        // The SignalR send can fail (network race, reconnecting transport). The Abort()
        // must still happen — otherwise a misbehaving notify path would let a revoked
        // user keep their socket open indefinitely.
        var db = NodePilot.TestCommons.TestDbFactory.Create();
        var jti = Guid.NewGuid().ToString();
        db.RevokedTokens.Add(new RevokedToken
        {
            Jti = jti,
            UserId = Guid.NewGuid(),
            RevokedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
        });
        await db.SaveChangesAsync();

        var ctx = new Mock<HubCallerContext>();
        ExecutionHub.RegisterAuthForTest("conn-1", jti, Guid.NewGuid(), ctx.Object);

        var (sweeper, _, proxy, _) = Build(db);
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
             .ThrowsAsync(new InvalidOperationException("transport closed"));

        await sweeper.SweepOnceAsync(CancellationToken.None);

        ctx.Verify(c => c.Abort(), Times.Once,
            "Abort must run regardless of whether the cooperative notify reached the client");
    }

    [Fact]
    public async Task SweepOnceAsync_ConnectionNotInAuthMap_SilentlySkipsAbort()
    {
        // FindConnectionsToDrop yields ids based on the auth map. In a race the auth map
        // might already be cleared (OnDisconnectedAsync ran first). The sweeper must
        // not throw NullReferenceException — TryGetContext returning null means "no-op".
        var db = NodePilot.TestCommons.TestDbFactory.Create();
        var jti = Guid.NewGuid().ToString();
        db.RevokedTokens.Add(new RevokedToken
        {
            Jti = jti,
            UserId = Guid.NewGuid(),
            RevokedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
        });
        await db.SaveChangesAsync();

        var (sweeper, _, _, _) = Build(db);

        // No RegisterAuthForTest — the auth map is empty, so FindConnectionsToDrop yields
        // nothing → SweepOnceAsync exits early without throwing.
        await sweeper.SweepOnceAsync(CancellationToken.None);
    }
}
