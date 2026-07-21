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
using Xunit;

namespace NodePilot.Engine.Tests.Scheduler;

/// <summary>
/// Drives WorkflowStatsRefresher.RefreshOnceAsync against a seeded SQLite database
/// to validate the four GROUP BY queries + per-workflow upsert + percentile math.
/// The hot path on read endpoints depends on this row, so the aggregation must be
/// correct in detail.
/// </summary>
public class WorkflowStatsRefresherTests
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

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection().Build();

    private static Workflow Wf(string name) => new()
    {
        Id = Guid.NewGuid(), Name = name, DefinitionJson = "{}", IsEnabled = true,
    };

    private static WorkflowExecution Exec(Guid wfId, ExecutionStatus status, DateTime startedAt, double durationMs = 0)
    {
        return new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = wfId,
            Status = status,
            StartedAt = startedAt,
            CompletedAt = status is ExecutionStatus.Succeeded or ExecutionStatus.Failed or ExecutionStatus.Cancelled
                ? startedAt.AddMilliseconds(durationMs)
                : null,
        };
    }

    private static WorkflowStatsRefresher CreateService(IServiceScopeFactory factory) =>
        new(factory, EmptyConfig(),
            new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            NullLogger<WorkflowStatsRefresher>.Instance);


    [Fact]
    public async Task RefreshOnceAsync_NoExecutions_WritesZeroedStatsRow()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = Wf("Empty");
            db.Workflows.Add(wf);
            await db.SaveChangesAsync();

            var refreshed = await CreateService(factory).RefreshOnceAsync(7, CancellationToken.None);

            refreshed.Should().Be(1);
            var row = await db.WorkflowStats.AsNoTracking().FirstAsync();
            row.WorkflowId.Should().Be(wf.Id);
            row.TotalExecutions.Should().Be(0);
            row.SucceededWindow.Should().Be(0);
            row.FailedWindow.Should().Be(0);
            row.CancelledWindow.Should().Be(0);
            row.AvgDurationMsWindow.Should().BeNull();
            row.P50DurationMsWindow.Should().BeNull();
            row.P95DurationMsWindow.Should().BeNull();
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task RefreshOnceAsync_AggregatesStatusCountsInsideWindow()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = Wf("Mixed");
            db.Workflows.Add(wf);
            await db.SaveChangesAsync();

            var inWindow = DateTime.UtcNow.AddDays(-3);
            db.WorkflowExecutions.AddRange(
                Exec(wf.Id, ExecutionStatus.Succeeded, inWindow, 100),
                Exec(wf.Id, ExecutionStatus.Succeeded, inWindow, 200),
                Exec(wf.Id, ExecutionStatus.Failed, inWindow, 50),
                Exec(wf.Id, ExecutionStatus.Cancelled, inWindow, 10),
                // Outside window — must not influence *Window counters
                Exec(wf.Id, ExecutionStatus.Succeeded, DateTime.UtcNow.AddDays(-30), 999));
            await db.SaveChangesAsync();

            await CreateService(factory).RefreshOnceAsync(7, CancellationToken.None);

            var row = await db.WorkflowStats.AsNoTracking().FirstAsync();
            row.TotalExecutions.Should().Be(5, "all-time count includes the out-of-window run");
            row.SucceededWindow.Should().Be(2);
            row.FailedWindow.Should().Be(1);
            row.CancelledWindow.Should().Be(1);
            row.WindowDays.Should().Be(7);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task RefreshOnceAsync_CalculatesAvgAndPercentiles()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = Wf("Perf");
            db.Workflows.Add(wf);
            await db.SaveChangesAsync();

            var when = DateTime.UtcNow.AddHours(-1);
            // Durations: 100, 200, 300, 400, 500 → avg 300, p50 300, p95 480
            db.WorkflowExecutions.AddRange(
                Exec(wf.Id, ExecutionStatus.Succeeded, when, 100),
                Exec(wf.Id, ExecutionStatus.Succeeded, when.AddMinutes(1), 200),
                Exec(wf.Id, ExecutionStatus.Succeeded, when.AddMinutes(2), 300),
                Exec(wf.Id, ExecutionStatus.Succeeded, when.AddMinutes(3), 400),
                Exec(wf.Id, ExecutionStatus.Succeeded, when.AddMinutes(4), 500));
            await db.SaveChangesAsync();

            await CreateService(factory).RefreshOnceAsync(7, CancellationToken.None);

            var row = await db.WorkflowStats.AsNoTracking().FirstAsync();
            row.AvgDurationMsWindow.Should().Be(300d);
            row.P50DurationMsWindow.Should().Be(300d);
            row.P95DurationMsWindow.Should().BeApproximately(480d, 0.01); // linear-interp percentile
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task RefreshOnceAsync_LastTimestampsTrackSucceededAndFailedSeparately()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = Wf("History");
            db.Workflows.Add(wf);
            await db.SaveChangesAsync();

            var lastSuccess = DateTime.UtcNow.AddHours(-2);
            var lastFailure = DateTime.UtcNow.AddHours(-1);
            db.WorkflowExecutions.AddRange(
                Exec(wf.Id, ExecutionStatus.Succeeded, DateTime.UtcNow.AddHours(-3), 50),
                Exec(wf.Id, ExecutionStatus.Succeeded, lastSuccess, 50),
                Exec(wf.Id, ExecutionStatus.Failed, DateTime.UtcNow.AddHours(-4), 20),
                Exec(wf.Id, ExecutionStatus.Failed, lastFailure, 20));
            await db.SaveChangesAsync();

            await CreateService(factory).RefreshOnceAsync(7, CancellationToken.None);

            var row = await db.WorkflowStats.AsNoTracking().FirstAsync();
            row.LastSuccessAt.Should().BeCloseTo(lastSuccess, TimeSpan.FromSeconds(2));
            row.LastFailureAt.Should().BeCloseTo(lastFailure, TimeSpan.FromSeconds(2));
            row.LastExecutionAt.Should().BeCloseTo(lastFailure, TimeSpan.FromSeconds(2),
                "last *any* status — newest execution wins");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task RefreshOnceAsync_RemovesOrphanStatsRowsForDeletedWorkflows()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            // Workflow that lives long enough to seed a stats row, then gets deleted.
            var live = Wf("Live");
            var deleted = Wf("Deleted");
            db.Workflows.AddRange(live, deleted);
            await db.SaveChangesAsync();

            var svc = CreateService(factory);
            await svc.RefreshOnceAsync(7, CancellationToken.None);

            db.Workflows.Remove(deleted);
            await db.SaveChangesAsync();

            await svc.RefreshOnceAsync(7, CancellationToken.None);

            var rows = await db.WorkflowStats.AsNoTracking().Select(s => s.WorkflowId).ToListAsync();
            rows.Should().HaveCount(1);
            rows.Should().ContainSingle(r => r == live.Id);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task RefreshOnceAsync_MultipleWorkflows_ScopesAggregationsCorrectly()
    {
        // Critical regression guard: a faulty GROUP BY would leak counts between workflows.
        var (db, factory, conn) = CreateEnv();
        try
        {
            var a = Wf("A");
            var b = Wf("B");
            db.Workflows.AddRange(a, b);
            await db.SaveChangesAsync();

            var when = DateTime.UtcNow.AddHours(-1);
            db.WorkflowExecutions.AddRange(
                Exec(a.Id, ExecutionStatus.Succeeded, when, 100),
                Exec(a.Id, ExecutionStatus.Succeeded, when, 100),
                Exec(b.Id, ExecutionStatus.Failed, when, 50));
            await db.SaveChangesAsync();

            await CreateService(factory).RefreshOnceAsync(7, CancellationToken.None);

            var rowA = await db.WorkflowStats.AsNoTracking().FirstAsync(r => r.WorkflowId == a.Id);
            var rowB = await db.WorkflowStats.AsNoTracking().FirstAsync(r => r.WorkflowId == b.Id);
            rowA.TotalExecutions.Should().Be(2);
            rowA.SucceededWindow.Should().Be(2);
            rowA.FailedWindow.Should().Be(0);
            rowB.TotalExecutions.Should().Be(1);
            rowB.SucceededWindow.Should().Be(0);
            rowB.FailedWindow.Should().Be(1);
        }
        finally { conn.Dispose(); }
    }
}
