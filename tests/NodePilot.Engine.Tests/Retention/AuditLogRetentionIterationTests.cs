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
/// One sweep pass of <see cref="AuditLogRetentionService"/>. Audit retention is the one sweep
/// where a misconfiguration is compliance-relevant: the floor of 30 days must hold even when an
/// operator types a smaller number, and a live Enabled=false has to park the sweep rather than
/// require a restart. The heartbeat write is deliberately not asserted: SystemHealthWriter
/// debounces through a process-static map keyed by service name, so whether a given pass
/// writes depends on what other tests in the same run did — an order-dependent assertion. <see
/// cref="AuditLogRetentionServiceTests"/> covers the archive writing.
/// </summary>
public sealed class AuditLogRetentionIterationTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly NodePilotDbContext _db;
    private readonly ServiceProvider _services;

    public AuditLogRetentionIterationTests()
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
    public async Task RunIterationAsync_Disabled_ParksTheSweep()
    {
        SeedEntries(ageDays: 400, count: 5);

        await Service(new AuditLogRetentionOptions { Enabled = false, MaxAgeDays = 30 })
            .RunIterationAsync(TestContext.Current.CancellationToken);

        (await EntryCountAsync()).Should().Be(5);
    }

    [Fact]
    public async Task RunIterationAsync_Enabled_DeletesEntriesPastTheRetentionAge()
    {
        SeedEntries(ageDays: 400, count: 4);
        SeedEntries(ageDays: 10, count: 3);

        await Service(new AuditLogRetentionOptions
        {
            Enabled = true, MaxAgeDays = 365, BatchSize = 100,
        }).RunIterationAsync(TestContext.Current.CancellationToken);

        (await EntryCountAsync()).Should().Be(3, "only entries older than the cutoff are swept");
    }

    [Fact]
    public async Task RunIterationAsync_MaxAgeBelowThirtyDays_IsClampedToTheComplianceFloor()
    {
        SeedEntries(ageDays: 20, count: 3);

        await Service(new AuditLogRetentionOptions
        {
            Enabled = true, MaxAgeDays = 1, BatchSize = 100,
        }).RunIterationAsync(TestContext.Current.CancellationToken);

        (await EntryCountAsync()).Should().Be(3,
            "a 1-day retention must not be honoured — the floor is 30 days");
    }

    [Fact]
    public async Task RunIterationAsync_NothingOldEnough_LeavesEverythingInPlace()
    {
        SeedEntries(ageDays: 5, count: 6);

        await Service(new AuditLogRetentionOptions
        {
            Enabled = true, MaxAgeDays = 365, BatchSize = 100,
        }).RunIterationAsync(TestContext.Current.CancellationToken);

        (await EntryCountAsync()).Should().Be(6);
    }

    [Fact]
    public async Task RunIterationAsync_SweepFailure_IsSwallowedSoTheNextIntervalCanRetry()
    {
        SeedEntries(ageDays: 400, count: 2);
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(opts => opts.UseSqlite(_connection));
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var service = new AuditLogRetentionService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<RetentionOptions>(new RetentionOptions
            {
                AuditLog = new AuditLogRetentionOptions
                {
                    Enabled = true, MaxAgeDays = 365, BatchSize = 100,
                },
            }),
            new SingleNodeClusterStateProvider(),
            NullLogger<AuditLogRetentionService>.Instance,
            NodePilot.TestCommons.TestDatabaseAvailability.Available);
        await provider.DisposeAsync();

        var act = () => service.RunIterationAsync(TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    // ---------------------------------------------------------------- helpers

    private void SeedEntries(int ageDays, int count)
    {
        for (var i = 0; i < count; i++)
        {
            _db.AuditLog.Add(new AuditLogEntry
            {
                Id = Guid.NewGuid(),
                Action = "TEST_ACTION",
                Timestamp = DateTime.UtcNow.AddDays(-ageDays).AddSeconds(-i),
                Username = "tester",
            });
        }
        _db.SaveChanges();
    }

    private async Task<int> EntryCountAsync()
    {
        _db.ChangeTracker.Clear();
        return await _db.AuditLog.AsNoTracking().CountAsync(TestContext.Current.CancellationToken);
    }

    private AuditLogRetentionService Service(AuditLogRetentionOptions options) => new(
        _services.GetRequiredService<IServiceScopeFactory>(),
        new StaticOptionsMonitor<RetentionOptions>(new RetentionOptions { AuditLog = options }),
        new SingleNodeClusterStateProvider(),
        NullLogger<AuditLogRetentionService>.Instance, NodePilot.TestCommons.TestDatabaseAvailability.Available);
}
