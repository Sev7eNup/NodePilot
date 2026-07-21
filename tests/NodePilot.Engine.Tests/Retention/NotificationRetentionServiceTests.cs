using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Cluster;
using NodePilot.Scheduler;
using NodePilot.Scheduler.Options;
using Xunit;
using NodePilot.TestCommons;

namespace NodePilot.Engine.Tests.Retention;

public class NotificationRetentionServiceTests
{
    private static (NodePilotDbContext db, IServiceScopeFactory factory, SqliteConnection conn) CreateEnv()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(o => o.UseSqlite(conn));
        var sp = services.BuildServiceProvider();
        var outerDb = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(conn).Options);
        outerDb.Database.EnsureCreated();
        return (outerDb, sp.GetRequiredService<IServiceScopeFactory>(), conn);
    }

    private static NotificationRetentionService Service(IServiceScopeFactory factory, RetentionOptions? options = null)
        => new(factory, new StaticOptionsMonitor<RetentionOptions>(options ?? new RetentionOptions()),
            new SingleNodeClusterStateProvider(), NullLogger<NotificationRetentionService>.Instance);

    private static void AddAttempt(NodePilotDbContext db, NotificationDeliveryStatus status, DateTime createdAt)
        => db.NotificationDeliveryAttempts.Add(new NotificationDeliveryAttempt
        {
            Id = Guid.NewGuid(),
            NotificationRuleId = Guid.NewGuid(),
            NotificationRouteId = Guid.NewGuid(),
            EventKey = $"exec:{Guid.NewGuid():N}:ExecutionFailed",
            DedupKey = "k",
            Status = status,
            CreatedAt = createdAt,
        });

    [Fact]
    public async Task PurgeOnce_DeletesOldTerminalAttempts_KeepsPendingAndRecent()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            AddAttempt(db, NotificationDeliveryStatus.Sent, DateTime.UtcNow.AddDays(-100));   // old terminal → delete
            AddAttempt(db, NotificationDeliveryStatus.Failed, DateTime.UtcNow.AddDays(-100)); // old terminal → delete
            AddAttempt(db, NotificationDeliveryStatus.Pending, DateTime.UtcNow.AddDays(-100)); // old but Pending → keep
            AddAttempt(db, NotificationDeliveryStatus.Sent, DateTime.UtcNow.AddDays(-1));      // recent → keep
            await db.SaveChangesAsync();

            var deleted = await Service(factory).PurgeOnceAsync(maxAgeDays: 90, CancellationToken.None);

            deleted.Should().Be(2);
            (await db.NotificationDeliveryAttempts.CountAsync()).Should().Be(2);
            (await db.NotificationDeliveryAttempts.CountAsync(a => a.Status == NotificationDeliveryStatus.Pending)).Should().Be(1, "in-flight retries must never be pruned");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task PurgeOnce_PrunesStaleSuppressionStates_KeepsRecent()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            db.NotificationSuppressionStates.Add(new NotificationSuppressionState
            { Id = Guid.NewGuid(), NotificationRuleId = Guid.NewGuid(), DedupKey = "old", LastFiredAt = DateTime.UtcNow.AddDays(-100) });
            db.NotificationSuppressionStates.Add(new NotificationSuppressionState
            { Id = Guid.NewGuid(), NotificationRuleId = Guid.NewGuid(), DedupKey = "recent", LastFiredAt = DateTime.UtcNow.AddDays(-1) });
            db.NotificationSuppressionStates.Add(new NotificationSuppressionState
            { Id = Guid.NewGuid(), NotificationRuleId = Guid.NewGuid(), DedupKey = "never-fired", LastFiredAt = null });
            await db.SaveChangesAsync();

            var deleted = await Service(factory).PurgeOnceAsync(maxAgeDays: 90, CancellationToken.None);

            deleted.Should().Be(1);
            (await db.NotificationSuppressionStates.CountAsync()).Should().Be(2, "recent + never-fired suppression rows survive");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task PurgeOnce_PrunesStaleSystemAlertPolicyState_KeepsRecent()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            db.SystemAlertPolicyStates.Add(new SystemAlertPolicyState
            { Id = Guid.NewGuid(), NotificationRuleId = Guid.NewGuid(), SourceId = "backlog", InstanceKey = "stale", LastObservedAt = DateTime.UtcNow.AddDays(-100) });
            db.SystemAlertPolicyStates.Add(new SystemAlertPolicyState
            { Id = Guid.NewGuid(), NotificationRuleId = Guid.NewGuid(), SourceId = "backlog", InstanceKey = "fresh", LastObservedAt = DateTime.UtcNow.AddHours(-1) });
            await db.SaveChangesAsync();

            var deleted = await Service(factory).PurgeOnceAsync(maxAgeDays: 90, CancellationToken.None);

            deleted.Should().Be(1);
            db.SystemAlertPolicyStates.Single().InstanceKey.Should().Be("fresh", "recently-observed instance state survives");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task PurgeOnce_NothingOld_ReturnsZero()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            AddAttempt(db, NotificationDeliveryStatus.Sent, DateTime.UtcNow.AddDays(-1));
            await db.SaveChangesAsync();

            (await Service(factory).PurgeOnceAsync(90, CancellationToken.None)).Should().Be(0);
            (await db.NotificationDeliveryAttempts.CountAsync()).Should().Be(1);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task RunIteration_Disabled_SkipsPurge()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            AddAttempt(db, NotificationDeliveryStatus.Sent, DateTime.UtcNow.AddDays(-100));
            await db.SaveChangesAsync();

            var options = new RetentionOptions { Notifications = { Enabled = false } };
            await Service(factory, options).RunIterationAsync(CancellationToken.None);

            (await db.NotificationDeliveryAttempts.CountAsync()).Should().Be(1, "a disabled sweep must not delete anything");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task RunIteration_Enabled_PurgesUsingConfiguredMaxAge()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            AddAttempt(db, NotificationDeliveryStatus.Sent, DateTime.UtcNow.AddDays(-10)); // older than custom 7d cutoff
            AddAttempt(db, NotificationDeliveryStatus.Sent, DateTime.UtcNow.AddDays(-1));  // survives
            await db.SaveChangesAsync();

            var options = new RetentionOptions { Notifications = { Enabled = true, MaxAgeDays = 7 } };
            await Service(factory, options).RunIterationAsync(CancellationToken.None);

            (await db.NotificationDeliveryAttempts.CountAsync()).Should().Be(1);
        }
        finally { conn.Dispose(); }
    }
}
