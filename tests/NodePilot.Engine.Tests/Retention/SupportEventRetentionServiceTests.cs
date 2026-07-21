using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Cluster;
using NodePilot.Scheduler;
using NodePilot.Scheduler.Options;
using Xunit;
using NodePilot.TestCommons;

namespace NodePilot.Engine.Tests.Retention;

public class SupportEventRetentionServiceTests
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

    private static SupportEventRetentionService Service(IServiceScopeFactory factory, RetentionOptions? options = null)
        => new(factory, new StaticOptionsMonitor<RetentionOptions>(options ?? new RetentionOptions()),
            new SingleNodeClusterStateProvider(), NullLogger<SupportEventRetentionService>.Instance);

    private static void AddEvent(NodePilotDbContext db, DateTime timestamp)
        => db.SupportEvents.Add(new SupportEvent
        {
            Id = Guid.NewGuid(),
            Timestamp = timestamp,
            Level = 3,
            EventType = "test.event",
            Message = "test",
        });

    [Fact]
    public async Task PurgeOnce_DeletesOldEvents_KeepsRecent()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            AddEvent(db, DateTime.UtcNow.AddDays(-100)); // old → delete
            AddEvent(db, DateTime.UtcNow.AddDays(-91));  // old → delete
            AddEvent(db, DateTime.UtcNow.AddDays(-1));   // recent → keep
            await db.SaveChangesAsync();

            var deleted = await Service(factory).PurgeOnceAsync(maxAgeDays: 90, CancellationToken.None);

            deleted.Should().Be(2);
            (await db.SupportEvents.CountAsync()).Should().Be(1);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task PurgeOnce_NothingOld_ReturnsZero()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            AddEvent(db, DateTime.UtcNow.AddDays(-1));
            await db.SaveChangesAsync();

            (await Service(factory).PurgeOnceAsync(90, CancellationToken.None)).Should().Be(0);
            (await db.SupportEvents.CountAsync()).Should().Be(1);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task RunIteration_Disabled_SkipsPurge()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            AddEvent(db, DateTime.UtcNow.AddDays(-100));
            await db.SaveChangesAsync();

            var options = new RetentionOptions { SupportEvents = { Enabled = false } };
            await Service(factory, options).RunIterationAsync(CancellationToken.None);

            (await db.SupportEvents.CountAsync()).Should().Be(1, "a disabled sweep must not delete anything");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task RunIteration_Enabled_PurgesUsingConfiguredMaxAge()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            AddEvent(db, DateTime.UtcNow.AddDays(-10)); // older than custom 7d cutoff
            AddEvent(db, DateTime.UtcNow.AddDays(-1));  // survives
            await db.SaveChangesAsync();

            var options = new RetentionOptions { SupportEvents = { Enabled = true, MaxAgeDays = 7 } };
            await Service(factory, options).RunIterationAsync(CancellationToken.None);

            (await db.SupportEvents.CountAsync()).Should().Be(1);
        }
        finally { conn.Dispose(); }
    }
}
