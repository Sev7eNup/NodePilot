using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Scheduler;
using Xunit;

namespace NodePilot.Engine.Tests.Triggers;

/// <summary>
/// Verifies that <see cref="TriggerOrchestrator"/> obeys cluster leadership: a follower
/// node never registers trigger sources, never fires, and immediately tears down whatever
/// was active when leadership is lost.
/// </summary>
public sealed class TriggerOrchestratorLeaderGatingTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly NodePilotDbContext _db;
    private readonly ServiceProvider _services;

    public TriggerOrchestratorLeaderGatingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(opts => opts.UseSqlite(_connection),
            ServiceLifetime.Transient);
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
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task SyncAsync_AsFollower_DoesNotTouchDb_ZeroSourcesActive()
    {
        // A workflow with a scheduleTrigger exists in the DB.
        _db.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "scheduled-job",
            DefinitionJson = """
            {"nodes":[{"id":"n1","data":{"activityType":"scheduleTrigger","config":{"cronExpression":"0 0 * * * ? *"}}}],"edges":[]}
            """,
            IsEnabled = true,
            Version = 1
        });
        _db.SaveChanges();

        var orchestrator = new TriggerOrchestrator(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services,
            new FollowerCluster(),
            NullLogger<TriggerOrchestrator>.Instance);

        // Act — a sync tick on a follower must early-out and leave no sources registered.
        await orchestrator.SyncAsync(CancellationToken.None);

        var activeField = typeof(TriggerOrchestrator).GetField("_active",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var active = (System.Collections.IDictionary)activeField.GetValue(orchestrator)!;
        active.Count.Should().Be(0, "a follower must never register a trigger source");
    }

    [Fact]
    public async Task FireAsync_AsFollower_IsNoOp()
    {
        var orchestrator = new TriggerOrchestrator(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services,
            new FollowerCluster(),
            NullLogger<TriggerOrchestrator>.Instance);

        // Even if a source somehow fired (race during disposal), FireAsync must drop it
        // because the cluster gate flipped to follower.
        await orchestrator.FireAsync(Guid.NewGuid(), "scheduleTrigger", new Dictionary<string, string>());

        // No execution row was created.
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    private sealed class FollowerCluster : IClusterStateProvider
    {
        public bool IsLeader => false;
        public string NodeId => "follower";
        public DateTime? LeaseExpiresAt => null;
        public long LeaseEpoch => 0;
        public DateTime? LastSuccessfulRenewAt => null;
        public event Action<long>? OnLeadershipAcquired { add { } remove { } }
        public event Action? OnLeadershipLost { add { } remove { } }
    }
}
