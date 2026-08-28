using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Diagnostics;
using NodePilot.Api.Hosting;
using NodePilot.Api.Security;
using NodePilot.Api.Tests.TestSupport;
using NodePilot.Core.Exceptions;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Availability;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

/// <summary>
/// Tests for hosted services and diagnostics helpers: session retention, the support-event
/// flush loop, support-log path resolution, and the mapping from capacity exceptions to 503.
/// </summary>
public sealed class BackgroundServiceAndDiagnosticsTests
{
    // ---------------------------------------------------------------- AuthSessionCleanupService

    [Fact]
    public async Task SweepOnceAsync_RemovesExpiredSessions()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db);
        db.AuthSessions.Add(Session(userId, expiresAt: DateTime.UtcNow.AddHours(-1)));
        db.AuthSessions.Add(Session(userId, expiresAt: DateTime.UtcNow.AddHours(+1)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var deleted = await Cleanup(db).SweepOnceAsync(TestContext.Current.CancellationToken);

        deleted.Should().Be(1);
        (await db.AuthSessions.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task SweepOnceAsync_RemovesSessionsRevokedMoreThanSevenDaysAgo()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db);
        var stale = Session(userId, expiresAt: DateTime.UtcNow.AddDays(30));
        stale.RevokedAt = DateTime.UtcNow.AddDays(-8);
        var recentlyRevoked = Session(userId, expiresAt: DateTime.UtcNow.AddDays(30));
        recentlyRevoked.RevokedAt = DateTime.UtcNow.AddDays(-1);
        db.AuthSessions.AddRange(stale, recentlyRevoked);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var deleted = await Cleanup(db).SweepOnceAsync(TestContext.Current.CancellationToken);

        deleted.Should().Be(1, "a freshly revoked session is still needed for audit correlation");
        (await db.AuthSessions.SingleAsync(TestContext.Current.CancellationToken))
            .RevokedAt.Should().BeCloseTo(recentlyRevoked.RevokedAt!.Value, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SweepOnceAsync_RemovesExpiredOidcLoginTickets()
    {
        using var db = TestDbFactory.Create();
        db.OidcLoginTickets.Add(new OidcLoginTicket
        {
            Id = "expired", ProtectedPayload = [1, 2, 3], ExpiresAt = DateTime.UtcNow.AddMinutes(-1),
        });
        db.OidcLoginTickets.Add(new OidcLoginTicket
        {
            Id = "live", ProtectedPayload = [4, 5, 6], ExpiresAt = DateTime.UtcNow.AddMinutes(5),
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var deleted = await Cleanup(db).SweepOnceAsync(TestContext.Current.CancellationToken);

        deleted.Should().Be(1);
        (await db.OidcLoginTickets.SingleAsync(TestContext.Current.CancellationToken))
            .Id.Should().Be("live");
    }

    [Fact]
    public async Task SweepOnceAsync_WithNothingToDo_ReturnsZero()
    {
        using var db = TestDbFactory.Create();

        var deleted = await Cleanup(db).SweepOnceAsync(TestContext.Current.CancellationToken);

        deleted.Should().Be(0);
    }

    // ---------------------------------------------------------------- SupportEventFlushService

    [Fact]
    public async Task SupportEventFlush_PersistsQueuedEvents()
    {
        // The service writes from its own thread, so it gets its own context over the same
        // SQLite connection — sharing one DbContext across both threads is a data race that
        // can make this test flaky under a full parallel run.
        var (connection, readContext) = TestDbFactory.CreateWithConnection();
        using var _ = connection;
        using var db = readContext;
        using var serviceDb = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(connection).Options);

        var channel = new SupportEventChannel();
        channel.TryWrite(Event("first"));
        channel.TryWrite(Event("second"));

        var service = FlushService(serviceDb, channel);
        await service.StartAsync(CancellationToken.None);
        await WaitForAsync(async () =>
            await db.SupportEvents.CountAsync(TestContext.Current.CancellationToken) == 2);
        await service.StopAsync(CancellationToken.None);

        var stored = await db.SupportEvents.OrderBy(x => x.Message)
            .ToListAsync(TestContext.Current.CancellationToken);
        stored.Select(x => x.Message).Should().Equal("first", "second");
    }

    [Fact]
    public async Task SupportEventFlush_KnownOutage_DropsWithoutDbWrite_ThenPersistsOneRecoverySummary()
    {
        var (connection, readContext) = TestDbFactory.CreateWithConnection();
        using var _ = connection;
        using var db = readContext;
        using var serviceDb = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(connection).Options);
        var availability = new DatabaseAvailabilityTracker(
            NullLogger<DatabaseAvailabilityTracker>.Instance);
        availability.MarkBootComplete();
        availability.ReportUnreachable(DatabaseOutageReason.Unreachable);
        var channel = new SupportEventChannel();
        channel.TryWrite(Event("lost-one"));
        channel.TryWrite(Event("lost-two"));
        var service = FlushService(serviceDb, channel, availability);

        await service.StartAsync(CancellationToken.None);
        await WaitForAsync(() => Task.FromResult(service.DroppedDuringCurrentOutage == 2));
        (await db.SupportEvents.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0,
            "a known outage must not generate another doomed database write");

        availability.ReportProbeSucceeded();
        availability.ReportProbeSucceeded();
        await WaitForAsync(async () =>
            await db.SupportEvents.CountAsync(TestContext.Current.CancellationToken) == 1);
        await service.StopAsync(CancellationToken.None);

        var summary = await db.SupportEvents.SingleAsync(TestContext.Current.CancellationToken);
        summary.EventType.Should().Be("DATABASE_OUTAGE_RECOVERED");
        summary.Message.Should().Contain("2");
    }

    // ---------------------------------------------------------------- SupportLogFileResolver

    [Fact]
    public void SupportLogFileResolver_ExposesDirectoryAndSearchPattern()
    {
        var root = Directory.CreateTempSubdirectory("np-support-log").FullName;
        try
        {
            var resolver = Resolver(root);

            resolver.Directory.Should().NotBeNullOrWhiteSpace();
            resolver.FileSearchPattern.Should().StartWith("nodepilot-support-").And.EndWith(".log");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GetFileForDate_ReturnsNullWhenTheDailyFileDoesNotExist()
    {
        var root = Directory.CreateTempSubdirectory("np-support-log").FullName;
        try
        {
            Resolver(root).GetFileForDate(new DateOnly(2026, 1, 1)).Should().BeNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GetFileForDate_ReturnsThePathOnceTheRolledFileExists()
    {
        var root = Directory.CreateTempSubdirectory("np-support-log").FullName;
        try
        {
            var resolver = Resolver(root);
            var date = new DateOnly(2026, 5, 15);
            var expected = Path.Combine(
                resolver.Directory,
                resolver.FileSearchPattern.Replace("*", date.ToString("yyyyMMdd")));
            Directory.CreateDirectory(resolver.Directory);
            File.WriteAllText(expected, "log line");

            resolver.GetFileForDate(date).Should().Be(expected);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GetCurrentDayFile_ReturnsNullWhenNothingHasBeenLoggedToday()
    {
        var root = Directory.CreateTempSubdirectory("np-support-log").FullName;
        try
        {
            Resolver(root).GetCurrentDayFile().Should().BeNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---------------------------------------------------------------- CapacityExceptionHandler

    [Fact]
    public async Task CapacityExceptionHandler_MapsCapacityExceptionTo503WithRetryAfter()
    {
        var handler = new CapacityExceptionHandler();
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var handled = await handler.TryHandleAsync(
            context, new ExecutionCapacityException("at capacity"), TestContext.Current.CancellationToken);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers["Retry-After"].ToString().Should().Be("30");
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(
            context.Response.Body, cancellationToken: TestContext.Current.CancellationToken);
        document.RootElement.GetProperty("message").GetString().Should().Be("at capacity");
    }

    [Fact]
    public async Task CapacityExceptionHandler_LeavesUnrelatedExceptionsToTheDefaultPipeline()
    {
        var handler = new CapacityExceptionHandler();
        var context = new DefaultHttpContext();

        var handled = await handler.TryHandleAsync(
            context, new InvalidOperationException("boom"), TestContext.Current.CancellationToken);

        handled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK, "the handler must not touch the response");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>AuthSession.UserId is a real FK — SQLite enforces it, so the row must
    /// exist.</summary>
    private static Guid SeedUser(NodePilotDbContext db)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "session-owner",
            Provider = NodePilot.Core.Enums.AuthProvider.Local,
            PasswordHash = "hash",
            Role = NodePilot.Core.Enums.UserRole.Admin,
            IsActive = true,
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user.Id;
    }

    private static AuthSession Session(Guid userId, DateTime expiresAt) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        AuthenticationMethod = "Local",
        CurrentJti = Guid.NewGuid().ToString("N"),
        CreatedAt = DateTime.UtcNow.AddHours(-2),
        ExpiresAt = expiresAt,
    };

    private static SupportEvent Event(string message) => new()
    {
        Id = Guid.NewGuid(),
        Timestamp = DateTime.UtcNow,
        Level = 2,
        EventType = "Test",
        Message = message,
    };

    private static AuthSessionCleanupService Cleanup(NodePilotDbContext db) => new(
        ScopeFactoryFor(db),
        NullLogger<AuthSessionCleanupService>.Instance,
        new LeaderClusterState(),
        NodePilot.TestCommons.TestDatabaseAvailability.Available);

    private static SupportEventFlushService FlushService(
        NodePilotDbContext db,
        SupportEventChannel channel,
        IDatabaseAvailability? availability = null)
    {
        var services = new ServiceCollection();
        // Singleton, not Scoped: a scoped registration makes the container own the context and
        // dispose it when the service's per-batch scope ends, which would kill the shared
        // test instance mid-run.
        services.AddSingleton(db);
        return new SupportEventFlushService(
            channel,
            services.BuildServiceProvider(),
            NullLogger<SupportEventFlushService>.Instance,
            availability ?? NodePilot.TestCommons.TestDatabaseAvailability.Available);
    }

    /// <summary>Polls a condition instead of sleeping a fixed span — keeps the loop tests fast and
    /// stable.</summary>
    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
    }

    private static IServiceScopeFactory ScopeFactoryFor(NodePilotDbContext db)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static ISupportLogFileResolver Resolver(string contentRoot)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        return new SupportLogFileResolver(configuration, new StubEnvironment(contentRoot, "Test"));
    }

    private sealed class LeaderClusterState : IClusterStateProvider
    {
        public bool IsLeader => true;
        public string NodeId => "test-node";
        public DateTime? LeaseExpiresAt => null;
        public long LeaseEpoch => 0;
        public DateTime? LastSuccessfulRenewAt => null;
        public event Action<long>? OnLeadershipAcquired { add { } remove { } }
        public event Action? OnLeadershipLost { add { } remove { } }
    }
}
