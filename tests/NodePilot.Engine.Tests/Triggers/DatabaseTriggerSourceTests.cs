using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Scheduler;
using NodePilot.Scheduler.Sources;
using Xunit;

namespace NodePilot.Engine.Tests.Triggers;

/// <summary>
/// Black-box tests for <see cref="DatabaseTriggerSource"/>. Uses a real shared-cache
/// SQLite DB as the watch target so we can mutate the sentinel between polls and verify
/// OnFire semantics. Config validation paths are isolated and don't require the DB.
/// </summary>
public class DatabaseTriggerSourceTests
{
    private static JsonElement ParseConfig(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection().Build();

    private static IConfiguration PermissiveConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Trigger:Database:RequireConnectionRef"] = "false",
        })
        .Build();

    private static IConfiguration RequireRefConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Trigger:Database:RequireConnectionRef"] = "true",
        })
        .Build();

    private static IConfiguration WithRegisteredConnection(string name, string connStr) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"Trigger:Database:Connections:{name}"] = connStr,
        })
        .Build();

    [Fact]
    public async Task StartAsync_Throws_WhenQueryMissing()
    {
        var src = new DatabaseTriggerSource(NullLogger<DatabaseTriggerSource>.Instance, EmptyConfig());
        var ctx = new TriggerContext
        {
            WorkflowId = Guid.NewGuid(),
            NodeId = "trg",
            Config = ParseConfig("""{"connectionString":"DataSource=:memory:"}"""),
            OnFire = _ => Task.CompletedTask,
        };

        var act = () => src.StartAsync(ctx, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*'query' is required*");
    }

    [Fact]
    public async Task StartAsync_Throws_WhenNeitherConnectionStringNorRefProvided()
    {
        var src = new DatabaseTriggerSource(NullLogger<DatabaseTriggerSource>.Instance, EmptyConfig());
        var ctx = new TriggerContext
        {
            WorkflowId = Guid.NewGuid(),
            NodeId = "trg",
            Config = ParseConfig("""{"query":"SELECT 1"}"""),
            OnFire = _ => Task.CompletedTask,
        };

        var act = () => src.StartAsync(ctx, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*either 'connectionString' or 'connectionRef'*");
    }

    [Fact]
    public async Task StartAsync_Throws_WhenQueryContainsTemplate()
    {
        // H-1 (security audit 2026-05-15): the trigger fires before any workflow run,
        // so {{var}} placeholders have no context to substitute. A literal placeholder
        // would either break the DB syntax at runtime or — if a future engine grows
        // pre-fire resolution — turn into an injection vector. Reject at registration time.
        var src = new DatabaseTriggerSource(NullLogger<DatabaseTriggerSource>.Instance, PermissiveConfig());
        var ctx = new TriggerContext
        {
            WorkflowId = Guid.NewGuid(),
            NodeId = "trg",
            Config = ParseConfig("""{"connectionString":"DataSource=:memory:","query":"SELECT MAX(Id) FROM T WHERE Tag = {{globals.WATCH_TAG}}"}"""),
            OnFire = _ => Task.CompletedTask,
        };

        var act = () => src.StartAsync(ctx, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*{{...}} templates*");
    }

    [Fact]
    public async Task StartAsync_Throws_WhenInlineConnectionUsedButRequireRefIsTrue()
    {
        var src = new DatabaseTriggerSource(NullLogger<DatabaseTriggerSource>.Instance, RequireRefConfig());
        var ctx = new TriggerContext
        {
            WorkflowId = Guid.NewGuid(),
            NodeId = "trg",
            Config = ParseConfig("""{"connectionString":"DataSource=:memory:","query":"SELECT 1"}"""),
            OnFire = _ => Task.CompletedTask,
        };

        var act = () => src.StartAsync(ctx, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*RequireConnectionRef=true*");
    }

    [Fact]
    public async Task StartAsync_Throws_WhenInlineConnectionUsedAndRequireRefIsMissing()
    {
        var src = new DatabaseTriggerSource(NullLogger<DatabaseTriggerSource>.Instance, EmptyConfig());
        var ctx = new TriggerContext
        {
            WorkflowId = Guid.NewGuid(),
            NodeId = "trg",
            Config = ParseConfig("""{"connectionString":"DataSource=:memory:","query":"SELECT 1"}"""),
            OnFire = _ => Task.CompletedTask,
        };

        var act = () => src.StartAsync(ctx, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*RequireConnectionRef=true*");
    }

    [Fact]
    public async Task StartAsync_AllowsInlineConnection_WhenRequireRefIsExplicitlyFalse()
    {
        var dbName = "trg-inline-" + Guid.NewGuid().ToString("N");
        var connStr = $"DataSource={dbName};Mode=Memory;Cache=Shared";
        await using var holder = new SqliteConnection(connStr);
        await holder.OpenAsync();
        await using (var ddl = holder.CreateCommand())
        {
            ddl.CommandText = "CREATE TABLE Marker (Id INTEGER PRIMARY KEY); INSERT INTO Marker VALUES (1);";
            await ddl.ExecuteNonQueryAsync();
        }

        var src = new DatabaseTriggerSource(NullLogger<DatabaseTriggerSource>.Instance, PermissiveConfig());
        var ctx = new TriggerContext
        {
            WorkflowId = Guid.NewGuid(),
            NodeId = "trg",
            Config = ParseConfig($$"""{"connectionString":"{{connStr}}","provider":"sqlite","query":"SELECT MAX(Id) FROM Marker","intervalSeconds":5}"""),
            OnFire = _ => Task.CompletedTask,
        };

        await src.StartAsync(ctx, CancellationToken.None);
        await src.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_Throws_WhenConnectionRefIsNotRegistered()
    {
        var src = new DatabaseTriggerSource(NullLogger<DatabaseTriggerSource>.Instance, EmptyConfig());
        var ctx = new TriggerContext
        {
            WorkflowId = Guid.NewGuid(),
            NodeId = "trg",
            Config = ParseConfig("""{"connectionRef":"GhostRef","query":"SELECT 1"}"""),
            OnFire = _ => Task.CompletedTask,
        };

        var act = () => src.StartAsync(ctx, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*connectionRef 'GhostRef' is not defined*");
    }

    [Fact]
    public async Task StartAsync_AcceptsRegisteredConnectionRef_AndStartsPolling()
    {
        // Use a shared-cache sqlite DB so the source's poll connection sees the same data as
        // the test setup. Plain DataSource=:memory: gives each connection its own fresh DB.
        var dbName = "trg-ref-" + Guid.NewGuid().ToString("N");
        var connStr = $"DataSource={dbName};Mode=Memory;Cache=Shared";

        // Hold one connection open for the duration so the shared in-memory DB persists.
        await using var holder = new SqliteConnection(connStr);
        await holder.OpenAsync();
        await using (var ddl = holder.CreateCommand())
        {
            ddl.CommandText = "CREATE TABLE Marker (Id INTEGER PRIMARY KEY); INSERT INTO Marker VALUES (1);";
            await ddl.ExecuteNonQueryAsync();
        }

        var src = new DatabaseTriggerSource(
            NullLogger<DatabaseTriggerSource>.Instance,
            WithRegisteredConnection("Marker", connStr));
        var ctx = new TriggerContext
        {
            WorkflowId = Guid.NewGuid(),
            NodeId = "trg",
            Config = ParseConfig("""{"connectionRef":"Marker","provider":"sqlite","query":"SELECT MAX(Id) FROM Marker","intervalSeconds":5}"""),
            OnFire = _ => Task.CompletedTask,
        };

        await src.StartAsync(ctx, CancellationToken.None);
        await src.DisposeAsync();
    }

    [Fact]
    public async Task PollLoop_FiresOnce_WhenSentinelChangesBetweenPolls()
    {
        var dbName = "trg-fire-" + Guid.NewGuid().ToString("N");
        var connStr = $"DataSource={dbName};Mode=Memory;Cache=Shared";
        await using var holder = new SqliteConnection(connStr);
        await holder.OpenAsync();
        await using (var ddl = holder.CreateCommand())
        {
            ddl.CommandText = "CREATE TABLE Q (Id INTEGER PRIMARY KEY AUTOINCREMENT, V TEXT); INSERT INTO Q (V) VALUES ('a');";
            await ddl.ExecuteNonQueryAsync();
        }

        var fires = new List<Dictionary<string, string>>();
        var fireGate = new SemaphoreSlim(0, 1);

        // intervalSeconds is clamped to a 5s minimum by the source. We use the
        // OnPollCompletedForTest hook to wait deterministically until the first poll
        // has seeded lastSentinel — mutating the table only AFTER that — so the
        // total wall time is dominated by a single 5s poll-interval, not by a
        // pessimistic 7s sleep that runs even when the poll completed in 50ms.
        var src = new DatabaseTriggerSource(
            NullLogger<DatabaseTriggerSource>.Instance,
            PermissiveConfig());

        var firstPollDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        src.OnPollCompletedForTest = () => firstPollDone.TrySetResult();

        var ctx = new TriggerContext
        {
            WorkflowId = Guid.NewGuid(),
            NodeId = "trg",
            Config = ParseConfig($$"""{"connectionString":"{{connStr}}","provider":"sqlite","query":"SELECT MAX(Id) FROM Q","intervalSeconds":5}"""),
            OnFire = parameters =>
            {
                lock (fires) fires.Add(parameters);
                fireGate.Release();
                return Task.CompletedTask;
            },
        };

        await src.StartAsync(ctx, CancellationToken.None);

        // Wait deterministically for the first poll to complete (no fire — just
        // seeded lastSentinel), then mutate.
        var firstPollCompleted = await Task.WhenAny(firstPollDone.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        firstPollCompleted.Should().Be(firstPollDone.Task, "the first poll must run before we mutate the table");

        await using (var insert = holder.CreateCommand())
        {
            insert.CommandText = "INSERT INTO Q (V) VALUES ('b');";
            await insert.ExecuteNonQueryAsync();
        }

        var fired = await fireGate.WaitAsync(TimeSpan.FromSeconds(10));
        await src.DisposeAsync();

        fired.Should().BeTrue("the second poll after the insert should have detected the sentinel change");
        fires.Should().HaveCountGreaterThanOrEqualTo(1);
        fires[0].Should().ContainKey("dbSentinel");
        fires[0].Should().ContainKey("dbPrevious");
        fires[0]["dbSentinel"].Should().NotBe(fires[0]["dbPrevious"]);
    }

    [Fact]
    public async Task DisposeAsync_StopsPollLoop_GracefullyWithoutPriorStart()
    {
        var src = new DatabaseTriggerSource(NullLogger<DatabaseTriggerSource>.Instance, EmptyConfig());

        // Should not throw even though StartAsync was never called.
        await src.DisposeAsync();
    }
}
