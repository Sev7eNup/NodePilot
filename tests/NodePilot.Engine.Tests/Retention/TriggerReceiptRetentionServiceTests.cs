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
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Engine.Tests.Retention;

/// <summary>
/// The receipt table gets one row per observed trigger signal, so it grows faster than any other
/// retention target. These cover the sweep itself plus the opt-out.
/// </summary>
public class TriggerReceiptRetentionServiceTests
{
    private static (NodePilotDbContext db, IServiceScopeFactory factory, SqliteConnection conn) CreateEnv()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(o => o.UseSqlite(conn));
        var sp = services.BuildServiceProvider();
        var outerDb = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(conn).Options);
        outerDb.Database.EnsureCreated();
        return (outerDb, sp.GetRequiredService<IServiceScopeFactory>(), conn);
    }

    private static TriggerReceiptRetentionService Service(
        IServiceScopeFactory factory, RetentionOptions? options = null)
        => new(factory, new StaticOptionsMonitor<RetentionOptions>(options ?? new RetentionOptions()),
            new SingleNodeClusterStateProvider(),
            NullLogger<TriggerReceiptRetentionService>.Instance,
            TestDatabaseAvailability.Available);

    private static Guid SeedWorkflow(NodePilotDbContext db)
    {
        var wf = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "WF",
            DefinitionJson = "{\"nodes\":[],\"edges\":[]}",
        };
        db.Workflows.Add(wf);
        db.SaveChanges();
        return wf.Id;
    }

    private static void AddReceipt(NodePilotDbContext db, Guid workflowId, DateTime receivedAt)
        => db.TriggerDeliveryReceipts.Add(new TriggerDeliveryReceipt
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflowId,
            TriggerNodeId = "trg-1",
            TriggerType = "scheduleTrigger",
            EventKey = Guid.NewGuid().ToString("N"),
            Outcome = "Dispatched",
            ReceivedAt = receivedAt,
        });

    [Fact]
    public async Task PurgeOnce_DeletesOldReceipts_KeepsRecent()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            AddReceipt(db, wf, DateTime.UtcNow.AddDays(-30));
            AddReceipt(db, wf, DateTime.UtcNow.AddDays(-8));
            AddReceipt(db, wf, DateTime.UtcNow.AddHours(-1));
            await db.SaveChangesAsync();

            var deleted = await Service(factory).PurgeOnceAsync(maxAgeDays: 7, CancellationToken.None);

            deleted.Should().Be(2);
            (await db.TriggerDeliveryReceipts.CountAsync()).Should().Be(1);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task PurgeOnce_NothingOld_ReturnsZero()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            AddReceipt(db, wf, DateTime.UtcNow.AddDays(-1));
            await db.SaveChangesAsync();

            (await Service(factory).PurgeOnceAsync(7, CancellationToken.None)).Should().Be(0);
            (await db.TriggerDeliveryReceipts.CountAsync()).Should().Be(1);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task RunIteration_Disabled_SkipsPurge()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            AddReceipt(db, wf, DateTime.UtcNow.AddDays(-30));
            await db.SaveChangesAsync();

            var options = new RetentionOptions { TriggerReceipts = { Enabled = false } };
            await Service(factory, options).RunIterationAsync(CancellationToken.None);

            (await db.TriggerDeliveryReceipts.CountAsync()).Should()
                .Be(1, "a disabled sweep must not delete anything");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task RunIteration_Enabled_PurgesUsingConfiguredMaxAge()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            AddReceipt(db, wf, DateTime.UtcNow.AddDays(-3)); // older than the custom 2d cutoff
            AddReceipt(db, wf, DateTime.UtcNow.AddHours(-1)); // survives
            await db.SaveChangesAsync();

            var options = new RetentionOptions { TriggerReceipts = { Enabled = true, MaxAgeDays = 2 } };
            await Service(factory, options).RunIterationAsync(CancellationToken.None);

            (await db.TriggerDeliveryReceipts.CountAsync()).Should().Be(1);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task PurgeOnce_LeavesCheckpointsUntouched()
    {
        // Checkpoints are one row per trigger node, updated in place. Deleting one would make the
        // source seed a fresh cursor on its next start, so the sweep must never touch them.
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            AddReceipt(db, wf, DateTime.UtcNow.AddDays(-30));
            db.TriggerDeliveryCheckpoints.Add(new TriggerDeliveryCheckpoint
            {
                WorkflowId = wf,
                TriggerNodeId = "trg-1",
                TriggerType = "scheduleTrigger",
                ConfigurationHash = "hash",
                Position = "pos",
                Version = "v",
                UpdatedAt = DateTime.UtcNow.AddDays(-30),
            });
            await db.SaveChangesAsync();

            await Service(factory).PurgeOnceAsync(7, CancellationToken.None);

            (await db.TriggerDeliveryReceipts.CountAsync()).Should().Be(0);
            (await db.TriggerDeliveryCheckpoints.CountAsync()).Should().Be(1);
        }
        finally { conn.Dispose(); }
    }
}
