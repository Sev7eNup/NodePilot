using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Scheduler;
using Xunit;
using NodePilot.TestCommons;

namespace NodePilot.Engine.Tests.Retention;

public class WorkflowVersionsRetentionServiceTests
{
    private static (NodePilotDbContext db, IServiceScopeFactory factory, SqliteConnection conn)
        CreateEnvironment()
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

    private static Workflow SeedWorkflow(NodePilotDbContext db, string name = "WF")
    {
        var wf = new Workflow { Id = Guid.NewGuid(), Name = name };
        db.Workflows.Add(wf);
        db.SaveChanges();
        return wf;
    }

    private static void SeedVersions(NodePilotDbContext db, Guid wfId, int count)
    {
        for (int i = 1; i <= count; i++)
        {
            db.WorkflowVersions.Add(new WorkflowVersion
            {
                Id = Guid.NewGuid(),
                WorkflowId = wfId,
                Version = i,
                Name = "WF",
                DefinitionJson = $"{{\"v\":{i}}}",
                CreatedAt = DateTime.UtcNow.AddDays(-count + i),
            });
        }
        db.SaveChanges();
    }

    [Fact]
    public async Task PurgeOnceAsync_KeepsLatestNPerWorkflow_DeletesOlderRows()
    {
        var (db, factory, conn) = CreateEnvironment();
        try
        {
            var wf = SeedWorkflow(db);
            SeedVersions(db, wf.Id, count: 15);

            var service = new WorkflowVersionsRetentionService(factory,
                new StaticOptionsMonitor<NodePilot.Scheduler.Options.RetentionOptions>(new NodePilot.Scheduler.Options.RetentionOptions()),
                new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
                NullLogger<WorkflowVersionsRetentionService>.Instance);

            var deleted = await service.PurgeOnceAsync(maxVersionsPerWorkflow: 5, batchSize: 100, CancellationToken.None);

            deleted.Should().Be(10);
            var survivors = await db.WorkflowVersions.OrderBy(v => v.Version).ToListAsync();
            survivors.Should().HaveCount(5);
            survivors.Select(v => v.Version).Should().Equal(11, 12, 13, 14, 15);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task PurgeOnceAsync_UnderThreshold_DeletesNothing()
    {
        // Workflow with N=3 versions and cap at 5 → nothing to delete.
        var (db, factory, conn) = CreateEnvironment();
        try
        {
            var wf = SeedWorkflow(db);
            SeedVersions(db, wf.Id, count: 3);

            var service = new WorkflowVersionsRetentionService(factory,
                new StaticOptionsMonitor<NodePilot.Scheduler.Options.RetentionOptions>(new NodePilot.Scheduler.Options.RetentionOptions()),
                new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
                NullLogger<WorkflowVersionsRetentionService>.Instance);

            var deleted = await service.PurgeOnceAsync(maxVersionsPerWorkflow: 5, batchSize: 100, CancellationToken.None);

            deleted.Should().Be(0);
            (await db.WorkflowVersions.CountAsync()).Should().Be(3);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task PurgeOnceAsync_AppliesCapPerWorkflow_NotGlobally()
    {
        // Two workflows with very different history sizes — trimming one must not touch
        // the other. This is the key property of per-workflow count-based retention.
        var (db, factory, conn) = CreateEnvironment();
        try
        {
            var heavy = SeedWorkflow(db, "heavy");
            var light = SeedWorkflow(db, "light");
            SeedVersions(db, heavy.Id, count: 20);
            SeedVersions(db, light.Id, count: 2);

            var service = new WorkflowVersionsRetentionService(factory,
                new StaticOptionsMonitor<NodePilot.Scheduler.Options.RetentionOptions>(new NodePilot.Scheduler.Options.RetentionOptions()),
                new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
                NullLogger<WorkflowVersionsRetentionService>.Instance);

            var deleted = await service.PurgeOnceAsync(maxVersionsPerWorkflow: 5, batchSize: 100, CancellationToken.None);

            deleted.Should().Be(15, "heavy had 20 → trim to 5 removes 15, light had only 2 so no-op");
            (await db.WorkflowVersions.Where(v => v.WorkflowId == heavy.Id).CountAsync()).Should().Be(5);
            (await db.WorkflowVersions.Where(v => v.WorkflowId == light.Id).CountAsync()).Should().Be(2);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task PurgeOnceAsync_BatchSize_CapsOnePass()
    {
        // batchSize bounds the PER-PASS deletion across all workflows. A single pass on a
        // backlog larger than batchSize leaves work for the next interval — prevents a
        // multi-second SQLite transaction on an eventually-consistent catch-up.
        var (db, factory, conn) = CreateEnvironment();
        try
        {
            var wf = SeedWorkflow(db);
            SeedVersions(db, wf.Id, count: 100);

            var service = new WorkflowVersionsRetentionService(factory,
                new StaticOptionsMonitor<NodePilot.Scheduler.Options.RetentionOptions>(new NodePilot.Scheduler.Options.RetentionOptions()),
                new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
                NullLogger<WorkflowVersionsRetentionService>.Instance);

            var deleted = await service.PurgeOnceAsync(maxVersionsPerWorkflow: 10, batchSize: 25, CancellationToken.None);

            deleted.Should().Be(25, "batch cap kicks in before the full 90-row overage is cleared");
            (await db.WorkflowVersions.CountAsync()).Should().Be(75);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task PurgeOnceAsync_EmptyDb_ReturnsZero()
    {
        var (db, factory, conn) = CreateEnvironment();
        try
        {
            var service = new WorkflowVersionsRetentionService(factory,
                new StaticOptionsMonitor<NodePilot.Scheduler.Options.RetentionOptions>(new NodePilot.Scheduler.Options.RetentionOptions()),
                new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
                NullLogger<WorkflowVersionsRetentionService>.Instance);

            var deleted = await service.PurgeOnceAsync(50, 100, CancellationToken.None);

            deleted.Should().Be(0);
        }
        finally { conn.Dispose(); }
    }
}
