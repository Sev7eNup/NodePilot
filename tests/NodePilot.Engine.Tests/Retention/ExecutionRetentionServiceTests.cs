using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Scheduler;
using NodePilot.Scheduler.Options;
using Xunit;
using NodePilot.TestCommons;

namespace NodePilot.Engine.Tests.Retention;

public class ExecutionRetentionServiceTests
{
    // Share a single SQLite connection with the service's scope factory so SaveChanges
    // in the service is visible through the outer db we use for assertions.
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

    private static Workflow SeedWorkflow(NodePilotDbContext db)
    {
        var wf = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "WF",
            DefinitionJson = "{\"nodes\":[],\"edges\":[]}"
        };
        db.Workflows.Add(wf);
        db.SaveChanges();
        return wf;
    }

    private static WorkflowExecution SeedExecution(
        NodePilotDbContext db, Guid wfId, ExecutionStatus status, DateTime startedAt)
    {
        var exec = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = wfId,
            Status = status,
            StartedAt = startedAt,
            CompletedAt = status == ExecutionStatus.Running ? null : startedAt.AddSeconds(1)
        };
        db.WorkflowExecutions.Add(exec);
        db.SaveChanges();
        return exec;
    }

    [Fact]
    public async Task PurgeOnceAsync_DeletesOldTerminalExecutions_AndCascadesSteps()
    {
        var (db, factory, conn) = CreateEnvironment();
        try
        {
            var wf = SeedWorkflow(db);
            var old = SeedExecution(db, wf.Id, ExecutionStatus.Succeeded, DateTime.UtcNow.AddDays(-60));
            // Child step — must be deleted via FK cascade when its parent is removed.
            db.StepExecutions.Add(new StepExecution
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = old.Id,
                StepId = "s1",
                StepType = "RunScript",
                Status = ExecutionStatus.Succeeded,
                StartedAt = old.StartedAt
            });
            var recent = SeedExecution(db, wf.Id, ExecutionStatus.Succeeded, DateTime.UtcNow.AddDays(-5));
            await db.SaveChangesAsync();

            var service = new ExecutionRetentionService(factory,
                new StaticOptionsMonitor<NodePilot.Scheduler.Options.RetentionOptions>(new NodePilot.Scheduler.Options.RetentionOptions()),
                new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
                NullLogger<ExecutionRetentionService>.Instance);

            var deleted = await service.PurgeOnceAsync(maxAgeDays: 30, batchSize: 100, CancellationToken.None);

            deleted.Should().Be(1);
            (await db.WorkflowExecutions.CountAsync()).Should().Be(1);
            (await db.WorkflowExecutions.FirstAsync()).Id.Should().Be(recent.Id);
            (await db.StepExecutions.CountAsync()).Should().Be(0, "FK cascade should remove child step when parent is deleted");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task PurgeOnceAsync_DoesNotTouchRunningRows_EvenIfOld()
    {
        // A Running row that is older than the cutoff — do not delete it. An in-flight
        // execution whose StartedAt drifted past the window (long-running workflow) must
        // not be ripped out from under the engine.
        var (db, factory, conn) = CreateEnvironment();
        try
        {
            var wf = SeedWorkflow(db);
            SeedExecution(db, wf.Id, ExecutionStatus.Running, DateTime.UtcNow.AddDays(-60));
            SeedExecution(db, wf.Id, ExecutionStatus.Pending, DateTime.UtcNow.AddDays(-60));
            SeedExecution(db, wf.Id, ExecutionStatus.Succeeded, DateTime.UtcNow.AddDays(-60));

            var service = new ExecutionRetentionService(factory,
                new StaticOptionsMonitor<NodePilot.Scheduler.Options.RetentionOptions>(new NodePilot.Scheduler.Options.RetentionOptions()),
                new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
                NullLogger<ExecutionRetentionService>.Instance);

            var deleted = await service.PurgeOnceAsync(30, 100, CancellationToken.None);

            deleted.Should().Be(1);
            (await db.WorkflowExecutions.CountAsync()).Should().Be(2, "Running and Pending rows must survive");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task PurgeOnceAsync_RespectsBatchSize_AcrossMultiplePasses()
    {
        var (db, factory, conn) = CreateEnvironment();
        try
        {
            var wf = SeedWorkflow(db);
            for (int i = 0; i < 7; i++)
                SeedExecution(db, wf.Id, ExecutionStatus.Failed, DateTime.UtcNow.AddDays(-40 - i));

            var service = new ExecutionRetentionService(factory,
                new StaticOptionsMonitor<NodePilot.Scheduler.Options.RetentionOptions>(new NodePilot.Scheduler.Options.RetentionOptions()),
                new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
                NullLogger<ExecutionRetentionService>.Instance);

            var deleted = await service.PurgeOnceAsync(maxAgeDays: 30, batchSize: 3, CancellationToken.None);

            // 7 candidates / batchSize 3 → two full batches (3+3) then a short batch (1) — all 7 deleted in one PurgeOnce call.
            deleted.Should().Be(7);
            (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task PurgeOnceAsync_NothingToDelete_ReturnsZero()
    {
        var (db, factory, conn) = CreateEnvironment();
        try
        {
            var wf = SeedWorkflow(db);
            SeedExecution(db, wf.Id, ExecutionStatus.Succeeded, DateTime.UtcNow.AddDays(-1));

            var service = new ExecutionRetentionService(factory,
                new StaticOptionsMonitor<NodePilot.Scheduler.Options.RetentionOptions>(new NodePilot.Scheduler.Options.RetentionOptions()),
                new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
                NullLogger<ExecutionRetentionService>.Instance);

            var deleted = await service.PurgeOnceAsync(30, 100, CancellationToken.None);

            deleted.Should().Be(0);
            (await db.WorkflowExecutions.CountAsync()).Should().Be(1);
        }
        finally { conn.Dispose(); }
    }

    /// <summary>
    /// With ArchivePath configured, a purge must append the deleted rows to a per-UTC-day
    /// NDJSON file BEFORE removing them (cold-storage hand-off). Exercises ValidateArchivePath
    /// (probe-write) + ArchiveAsync (NDJSON serialization) — both otherwise uncovered.
    /// </summary>
    [Fact]
    public async Task PurgeOnceAsync_WithArchivePath_WritesNdjsonThenDeletes()
    {
        var archiveDir = Path.Combine(Path.GetTempPath(), $"exec-archive-{Guid.NewGuid():N}");
        var (db, factory, conn) = CreateEnvironment();
        try
        {
            var wf = SeedWorkflow(db);
            var old1 = SeedExecution(db, wf.Id, ExecutionStatus.Succeeded, DateTime.UtcNow.AddDays(-40));
            var old2 = SeedExecution(db, wf.Id, ExecutionStatus.Failed, DateTime.UtcNow.AddDays(-50));
            await db.SaveChangesAsync();

            var opts = new NodePilot.Scheduler.Options.RetentionOptions();
            opts.Executions.ArchivePath = archiveDir;
            var service = new ExecutionRetentionService(factory,
                new StaticOptionsMonitor<NodePilot.Scheduler.Options.RetentionOptions>(opts),
                new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
                NullLogger<ExecutionRetentionService>.Instance);

            var deleted = await service.PurgeOnceAsync(maxAgeDays: 30, batchSize: 100, CancellationToken.None);

            deleted.Should().Be(2);
            (await db.WorkflowExecutions.CountAsync()).Should().Be(0);

            var files = Directory.GetFiles(archiveDir, "executions-*.ndjson");
            files.Should().HaveCount(1, "all deletions on the same UTC day roll into one file");
            var lines = (await File.ReadAllLinesAsync(files[0]))
                .Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            lines.Should().HaveCount(2, "one NDJSON line per archived execution");
            // The archive must carry the real row identity + status, not a placeholder.
            var joined = string.Join("\n", lines);
            joined.Should().Contain(old1.Id.ToString()).And.Contain(old2.Id.ToString());
            joined.Should().Contain("Succeeded").And.Contain("Failed");
        }
        finally
        {
            conn.Dispose();
            try { Directory.Delete(archiveDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// A configured-but-unwritable archive path must NOT block deletion — retention still
    /// removes rows (an unbounded DB is worse than a missing archive). Covers the
    /// ValidateArchivePath failure branch via an archive path that collides with an existing
    /// FILE (so Directory.CreateDirectory throws).
    /// </summary>
    [Fact]
    public async Task PurgeOnceAsync_UnwritableArchivePath_StillDeletes()
    {
        // Create a *file* and point ArchivePath at a child path of it — CreateDirectory then
        // throws IOException ("a file with the same name already exists"), exercising the
        // catch branch that disables archival but keeps deleting.
        var blockingFile = Path.Combine(Path.GetTempPath(), $"exec-archive-block-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(blockingFile, "x");
        var archiveDir = Path.Combine(blockingFile, "sub"); // child of a file → invalid dir
        var (db, factory, conn) = CreateEnvironment();
        try
        {
            var wf = SeedWorkflow(db);
            SeedExecution(db, wf.Id, ExecutionStatus.Succeeded, DateTime.UtcNow.AddDays(-40));
            await db.SaveChangesAsync();

            var opts = new NodePilot.Scheduler.Options.RetentionOptions();
            opts.Executions.ArchivePath = archiveDir;
            var service = new ExecutionRetentionService(factory,
                new StaticOptionsMonitor<NodePilot.Scheduler.Options.RetentionOptions>(opts),
                new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
                NullLogger<ExecutionRetentionService>.Instance);

            var deleted = await service.PurgeOnceAsync(maxAgeDays: 30, batchSize: 100, CancellationToken.None);

            deleted.Should().Be(1, "a broken archive path must not stop retention from deleting rows");
            (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
        }
        finally
        {
            conn.Dispose();
            try { File.Delete(blockingFile); } catch { }
        }
    }

    /// <summary>
    /// Hot-reload: RunIterationAsync reads IOptionsMonitor&lt;RetentionOptions&gt;.CurrentValue per
    /// pass, so a live toggle of Retention:Executions:Enabled takes effect without a restart.
    /// Drive the monitor (the test stand-in for a reloadOnChange config reload) from disabled→enabled
    /// between two iterations on the SAME service instance and assert the gate flips live.
    /// </summary>
    [Fact]
    public async Task RunIterationAsync_EnabledToggle_FlipsLiveAfterConfigReload()
    {
        var (db, factory, conn) = CreateEnvironment();
        try
        {
            var wf = SeedWorkflow(db);
            SeedExecution(db, wf.Id, ExecutionStatus.Succeeded, DateTime.UtcNow.AddDays(-60));
            await db.SaveChangesAsync();

            var monitor = new MutableOptionsMonitor<NodePilot.Scheduler.Options.RetentionOptions>(
                new NodePilot.Scheduler.Options.RetentionOptions
                {
                    Executions = new ExecutionsRetentionOptions { Enabled = false, MaxAgeDays = 30 }
                });
            var service = new ExecutionRetentionService(factory, monitor,
                new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
                NullLogger<ExecutionRetentionService>.Instance);

            // Disabled: the pass parks without deleting.
            await service.RunIterationAsync(CancellationToken.None);
            (await db.WorkflowExecutions.CountAsync()).Should().Be(1);

            // Operator enables Retention:Executions in the Settings UI → config reload.
            monitor.Set(new NodePilot.Scheduler.Options.RetentionOptions
            {
                Executions = new ExecutionsRetentionOptions { Enabled = true, MaxAgeDays = 30 }
            });

            // Same service instance, no restart: the next iteration now purges.
            await service.RunIterationAsync(CancellationToken.None);
            (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
        }
        finally { conn.Dispose(); }
    }

    /// <summary>
    /// Hot-reload: a live edit of MaxAgeDays changes the cutoff on the next pass. Seed rows at
    /// 40 and 60 days; with MaxAgeDays=30 both purge. Mutate to MaxAgeDays=50 mid-run and only
    /// the 60-day row is touched on the second pass.
    /// </summary>
    [Fact]
    public async Task RunIterationAsync_MaxAgeDaysMutation_ChangesCutoffLive()
    {
        var (db, factory, conn) = CreateEnvironment();
        try
        {
            var wf = SeedWorkflow(db);
            SeedExecution(db, wf.Id, ExecutionStatus.Succeeded, DateTime.UtcNow.AddDays(-60));
            var keepAtFifty = SeedExecution(db, wf.Id, ExecutionStatus.Succeeded, DateTime.UtcNow.AddDays(-40));
            await db.SaveChangesAsync();

            var monitor = new MutableOptionsMonitor<NodePilot.Scheduler.Options.RetentionOptions>(
                new NodePilot.Scheduler.Options.RetentionOptions
                {
                    Executions = new ExecutionsRetentionOptions { Enabled = true, MaxAgeDays = 50 }
                });
            var service = new ExecutionRetentionService(factory, monitor,
                new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
                NullLogger<ExecutionRetentionService>.Instance);

            // MaxAgeDays=50 → only the 60-day row is past the cutoff.
            await service.RunIterationAsync(CancellationToken.None);
            (await db.WorkflowExecutions.CountAsync()).Should().Be(1);
            (await db.WorkflowExecutions.FirstAsync()).Id.Should().Be(keepAtFifty.Id);

            // Operator tightens the window to 30 days → the 40-day row now also qualifies.
            monitor.Set(new NodePilot.Scheduler.Options.RetentionOptions
            {
                Executions = new ExecutionsRetentionOptions { Enabled = true, MaxAgeDays = 30 }
            });

            await service.RunIterationAsync(CancellationToken.None);
            (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
        }
        finally { conn.Dispose(); }
    }

    /// <summary>
    /// Hot-reload: a changed ArchivePath is re-probed on the next pass. Start with a broken path
    /// (child of a file → CreateDirectory throws) so the first pass deletes without archiving;
    /// mutate to a valid directory and assert the next pass writes the NDJSON archive — proving
    /// the cached "broken" verdict was invalidated by the path change.
    /// </summary>
    [Fact]
    public async Task RunIterationAsync_ArchivePathChange_ReprobesLive()
    {
        var blockingFile = Path.Combine(Path.GetTempPath(), $"exec-archive-block-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(blockingFile, "x");
        var validDir = Path.Combine(Path.GetTempPath(), $"exec-archive-{Guid.NewGuid():N}");
        var (db, factory, conn) = CreateEnvironment();
        try
        {
            var wf = SeedWorkflow(db);
            var old = SeedExecution(db, wf.Id, ExecutionStatus.Succeeded, DateTime.UtcNow.AddDays(-40));
            await db.SaveChangesAsync();

            var monitor = new MutableOptionsMonitor<NodePilot.Scheduler.Options.RetentionOptions>(
                new NodePilot.Scheduler.Options.RetentionOptions
                {
                    Executions = new ExecutionsRetentionOptions { Enabled = true, MaxAgeDays = 30, ArchivePath = Path.Combine(blockingFile, "sub") }
                });
            var service = new ExecutionRetentionService(factory, monitor,
                new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
                NullLogger<ExecutionRetentionService>.Instance);

            // First pass: broken archive path → deletes but writes no archive.
            await service.RunIterationAsync(CancellationToken.None);
            (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
            Directory.Exists(validDir).Should().BeFalse();

            // Seed a fresh old row, then point ArchivePath at a valid directory via config reload.
            var old2 = SeedExecution(db, wf.Id, ExecutionStatus.Succeeded, DateTime.UtcNow.AddDays(-40));
            await db.SaveChangesAsync();
            monitor.Set(new NodePilot.Scheduler.Options.RetentionOptions
            {
                Executions = new ExecutionsRetentionOptions { Enabled = true, MaxAgeDays = 30, ArchivePath = validDir }
            });

            // Same service instance: the path change invalidates the cache → re-probe succeeds → archive written.
            await service.RunIterationAsync(CancellationToken.None);
            (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
            var files = Directory.GetFiles(validDir, "executions-*.ndjson");
            files.Should().HaveCount(1);
            (await File.ReadAllTextAsync(files[0])).Should().Contain(old2.Id.ToString());
        }
        finally
        {
            conn.Dispose();
            try { File.Delete(blockingFile); } catch { }
            try { Directory.Delete(validDir, recursive: true); } catch { }
        }
    }
}
