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
/// One sweep pass of <see cref="WorkflowVersionsRetentionService"/>. The pass reads its config
/// live on every iteration, so an operator flipping <c>Retention:WorkflowVersions:Enabled</c>
/// must park the sweep rather than require a restart — and a failing pass must never take the
/// service down, because the next interval has to retry. The heartbeat write is deliberately
/// not asserted: SystemHealthWriter debounces through a process-static map keyed by service
/// name, so whether a given pass writes depends on what other tests in the same run did.
/// <see cref="WorkflowVersionsRetentionServiceTests"/> covers the purge arithmetic itself.
/// </summary>
public sealed class WorkflowVersionsRetentionIterationTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly NodePilotDbContext _db;
    private readonly ServiceProvider _services;

    public WorkflowVersionsRetentionIterationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(opts => opts.UseSqlite(_connection));
        services.AddLogging();
        _services = services.BuildServiceProvider();

        _db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync()
    {
        _db.Dispose();
        await _services.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task RunIterationAsync_Disabled_ParksTheSweepWithoutDeletingAnything()
    {
        var workflowId = SeedVersions(count: 10);

        await Service(new WorkflowVersionsRetentionOptions { Enabled = false, MaxVersionsPerWorkflow = 2 })
            .RunIterationAsync(TestContext.Current.CancellationToken);

        (await VersionCountAsync(workflowId)).Should().Be(10,
            "a live Enabled=false must park the sweep, not silently keep purging");
    }

    [Fact]
    public async Task RunIterationAsync_Enabled_PurgesDownToTheConfiguredCap()
    {
        var workflowId = SeedVersions(count: 10);

        await Service(new WorkflowVersionsRetentionOptions
        {
            Enabled = true, MaxVersionsPerWorkflow = 3, BatchSize = 100,
        }).RunIterationAsync(TestContext.Current.CancellationToken);

        (await VersionCountAsync(workflowId)).Should().Be(3);
    }

    [Fact]
    public async Task RunIterationAsync_KeepsTheNewestVersions()
    {
        var workflowId = SeedVersions(count: 5);

        await Service(new WorkflowVersionsRetentionOptions
        {
            Enabled = true, MaxVersionsPerWorkflow = 2, BatchSize = 100,
        }).RunIterationAsync(TestContext.Current.CancellationToken);

        _db.ChangeTracker.Clear();
        var remaining = await _db.WorkflowVersions.AsNoTracking()
            .Where(v => v.WorkflowId == workflowId)
            .Select(v => v.Version)
            .ToListAsync(TestContext.Current.CancellationToken);
        remaining.Should().BeEquivalentTo([4, 5], "retention drops the oldest history first");
    }

    [Fact]
    public async Task RunIterationAsync_ClampsAMaxVersionsBelowOne()
    {
        var workflowId = SeedVersions(count: 4);

        await Service(new WorkflowVersionsRetentionOptions
        {
            Enabled = true, MaxVersionsPerWorkflow = 0, BatchSize = 100,
        }).RunIterationAsync(TestContext.Current.CancellationToken);

        (await VersionCountAsync(workflowId)).Should().Be(1,
            "a misconfigured 0 must not wipe the entire version history");
    }

    [Fact]
    public async Task RunIterationAsync_SweepFailure_IsSwallowedSoTheNextIntervalCanRetry()
    {
        // A disposed provider makes the scope resolution throw inside the pass.
        SeedVersions(count: 3);
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(opts => opts.UseSqlite(_connection));
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var service = new WorkflowVersionsRetentionService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<RetentionOptions>(new RetentionOptions
            {
                WorkflowVersions = new WorkflowVersionsRetentionOptions
                {
                    Enabled = true, MaxVersionsPerWorkflow = 1, BatchSize = 100,
                },
            }),
            new SingleNodeClusterStateProvider(),
            NullLogger<WorkflowVersionsRetentionService>.Instance,
            NodePilot.TestCommons.TestDatabaseAvailability.Available);
        await provider.DisposeAsync();

        var act = () => service.RunIterationAsync(TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync(
            "a failing sweep must be logged and retried, never crash the hosted service");
    }

    // ---------------------------------------------------------------- helpers

    private Guid SeedVersions(int count)
    {
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "versioned",
            DefinitionJson = "{}",
        };
        _db.Workflows.Add(workflow);
        for (var version = 1; version <= count; version++)
        {
            _db.WorkflowVersions.Add(new WorkflowVersion
            {
                Id = Guid.NewGuid(),
                WorkflowId = workflow.Id,
                Version = version,
                Name = workflow.Name,
                DefinitionJson = "{}",
                CreatedAt = DateTime.UtcNow.AddMinutes(version),
            });
        }
        _db.SaveChanges();
        return workflow.Id;
    }

    private async Task<int> VersionCountAsync(Guid workflowId)
    {
        _db.ChangeTracker.Clear();
        return await _db.WorkflowVersions.AsNoTracking()
            .CountAsync(v => v.WorkflowId == workflowId, TestContext.Current.CancellationToken);
    }

    private WorkflowVersionsRetentionService Service(WorkflowVersionsRetentionOptions options) => new(
        _services.GetRequiredService<IServiceScopeFactory>(),
        new StaticOptionsMonitor<RetentionOptions>(new RetentionOptions { WorkflowVersions = options }),
        new SingleNodeClusterStateProvider(),
        NullLogger<WorkflowVersionsRetentionService>.Instance, NodePilot.TestCommons.TestDatabaseAvailability.Available);
}
