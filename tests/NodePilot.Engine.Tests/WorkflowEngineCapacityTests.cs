using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Core.Exceptions;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using Xunit;

namespace NodePilot.Engine.Tests;

/// <summary>
/// Covers the global and per-user execution caps in <see cref="WorkflowEngine.ExecuteAsync"/>
/// (tracked as security-audit finding H-3). The tests hold executions in-flight via a
/// semaphore so the capacity check fires deterministically, without relying on timing.
///
/// Each concurrent ExecuteAsync call gets a fresh engine and its own SQLite connection,
/// all pointing at the same shared-cache in-memory database. This mirrors the production
/// pattern (the DbContextPool hands out a separate connection per scope) and avoids
/// SQLite's ban on nested transactions, which would otherwise collide during the
/// workflow-end bulk save when several engines save in parallel.
/// </summary>
[Collection("SerialEngineTests")]
public sealed class WorkflowEngineCapacityTests : IDisposable
{
    // Trigger-only roots: the runnable fixture is rooted at a manualTrigger node. The
    // runScript node keeps its id ("step-1") so it still parks on the _gate latch; the
    // trigger completes immediately and passively (see the manualTrigger mock in the ctor).
    private const string SingleNodeWorkflowJson =
        "{\"nodes\":[" +
        "{\"id\":\"trigger-1\",\"type\":\"trigger\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"activityType\":\"manualTrigger\",\"config\":{}}}," +
        "{\"id\":\"step-1\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":120},\"data\":{\"activityType\":\"runScript\",\"config\":{}}}" +
        "],\"edges\":[{\"id\":\"te\",\"source\":\"trigger-1\",\"target\":\"step-1\"}]}";

    private readonly string _dataSource;
    private readonly SqliteConnection _keepAliveConnection;
    private readonly NodePilotDbContext _seedDb;
    private readonly ActivityRegistry _registry;
    private readonly Mock<IExecutionNotifier> _notifier;
    private readonly SemaphoreSlim _gate;
    private readonly ConcurrentBag<Guid> _startedExecutionIds;
    private IConfiguration _config = new ConfigurationBuilder().Build();

    public WorkflowEngineCapacityTests()
    {
        _gate = new SemaphoreSlim(0, int.MaxValue);
        _startedExecutionIds = new ConcurrentBag<Guid>();

        var executor = new Mock<IActivityExecutor>();
        executor.Setup(e => e.ActivityType).Returns("runScript");
        executor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .Returns<StepExecutionContext, JsonElement, CancellationToken>(async (ctx, _, ct) =>
            {
                _startedExecutionIds.Add(ctx.WorkflowExecutionId);
                await _gate.WaitAsync(ct);
                return new ActivityResult { Success = true, Output = "OK" };
            });

        // Trigger-only roots: a runnable fixture must be rooted at a manualTrigger node.
        // This mock is deliberately PASSIVE — it returns instantly and does NOT touch the
        // _gate latch or the _startedExecutionIds bag. The capacity assertions reason about
        // execution-level slots (per-user/global caps, in-flight executions) and count
        // in-flight via _startedExecutionIds, so the trigger must never perturb either:
        // it completes immediately, then the runScript step parks on the gate as before.
        var manualTriggerExecutor = new Mock<IActivityExecutor>();
        manualTriggerExecutor.Setup(e => e.ActivityType).Returns("manualTrigger");
        manualTriggerExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult { Success = true, Output = "{}" });

        _registry = new ActivityRegistry(new[] { executor.Object, manualTriggerExecutor.Object });

        // Per-test unique shared-cache in-memory DB; the keep-alive connection holds the
        // DB open for the test's lifetime (SQLite frees a shared-cache in-memory DB when
        // the last connection closes). Each NewEngine() opens its OWN connection to this
        // same DataSource so engines have isolated transactions — matches production
        // DbContextPool semantics.
        _dataSource = $"DataSource=file:np-capacity-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAliveConnection = new SqliteConnection(_dataSource);
        _keepAliveConnection.Open();

        _seedDb = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(_dataSource).Options);
        _seedDb.Database.EnsureCreated();

        _notifier = new Mock<IExecutionNotifier>();
    }

    public void Dispose()
    {
        // Release any executions still parked on the gate in case a test threw before
        // reaching Gate.Release — otherwise later tests would hang forever.
        try { _gate.Release(short.MaxValue); } catch (SemaphoreFullException) { }
        _seedDb.Dispose();
        _keepAliveConnection.Dispose();
    }

    private void ConfigureCaps(int? maxGlobal = null, int? maxPerUser = null)
    {
        var dict = new Dictionary<string, string?>();
        if (maxGlobal.HasValue) dict["Engine:MaxConcurrentExecutions:Global"] = maxGlobal.Value.ToString();
        if (maxPerUser.HasValue) dict["Engine:MaxConcurrentExecutions:PerUser"] = maxPerUser.Value.ToString();
        _config = new ConfigurationBuilder().AddInMemoryCollection(dict!).Build();
    }

    /// <summary>
    /// Builds a fresh engine and DbContext over the shared SQLite connection — one engine
    /// per concurrent ExecuteAsync call, so no DbContext is ever used from two calls at once.
    /// </summary>
    private WorkflowEngine NewEngine()
    {
        // The engine gets its own ServiceProvider with its own SQLite connection to the same
        // shared-cache in-memory database. Per-step DI scopes also connect via the connection
        // string (not a shared connection instance), so each scope resolution can open its
        // own transaction context.
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(opts => opts.UseSqlite(_dataSource));
        services.AddScoped(_ => _registry);
        var sp = services.BuildServiceProvider();

        var db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(_dataSource).Options);
        return new WorkflowEngine(db, _registry,
            NullLogger<WorkflowEngine>.Instance, sp, _notifier.Object, _config);
    }

    private static Workflow CreateWorkflow()
        => new() { Id = Guid.NewGuid(), Name = "Test", DefinitionJson = SingleNodeWorkflowJson };

    private async Task WaitForInFlightAsync(int expected, int timeoutMs = 60000)
    {
        // Two concurrent ExecuteAsync calls share the same SQLite connection — their
        // SaveChanges-Calls serialize on the connection lock. Under heavy CPU contention
        // (`dotnet test` runs the test DLLs in parallel, on a busy nightly host) this can
        // push the time-to-executor far past a tight budget — 15 s then 30 s ceilings both
        // still flaked under load (only 1 of N executions started in time). 60 s is generous
        // margin; the nightly also retries the whole backend suite once. We still fail fast
        // if executions hang permanently — this only widens the scheduling-starvation window.
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (_startedExecutionIds.Count < expected && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        _startedExecutionIds.Count.Should().BeGreaterThanOrEqualTo(expected,
            "expected {0} executions to reach the activity executor within {1} ms",
            expected, timeoutMs);
    }

    [Fact]
    public async Task ExecuteAsync_GlobalCapReached_ThrowsExecutionCapacityException()
    {
        ConfigureCaps(maxGlobal: 2);
        var wf = CreateWorkflow();
        _seedDb.Workflows.Add(wf);
        await _seedDb.SaveChangesAsync();

        var run1 = NewEngine().ExecuteAsync(wf, "test", CancellationToken.None);
        var run2 = NewEngine().ExecuteAsync(wf, "test", CancellationToken.None);
        await WaitForInFlightAsync(2);

        var act = async () => await NewEngine().ExecuteAsync(wf, "test", CancellationToken.None);
        await act.Should().ThrowAsync<ExecutionCapacityException>()
            .WithMessage("*Maximum concurrent workflow executions (2) reached*");

        _gate.Release(2);
        await Task.WhenAll(run1, run2);
    }

    [Fact]
    public async Task ExecuteAsync_PerUserCapReached_ThrowsForSameUser()
    {
        var userA = Guid.NewGuid();
        ConfigureCaps(maxGlobal: 100, maxPerUser: 2);
        var wf = CreateWorkflow();
        _seedDb.Workflows.Add(wf);
        await _seedDb.SaveChangesAsync();

        var run1 = NewEngine().ExecuteAsync(wf, "test", CancellationToken.None, startedByUserId: userA);
        var run2 = NewEngine().ExecuteAsync(wf, "test", CancellationToken.None, startedByUserId: userA);
        await WaitForInFlightAsync(2);

        var act = async () => await NewEngine().ExecuteAsync(wf, "test", CancellationToken.None, startedByUserId: userA);
        await act.Should().ThrowAsync<ExecutionCapacityException>()
            .WithMessage("*concurrent executions in flight (limit: 2)*");

        _gate.Release(2);
        await Task.WhenAll(run1, run2);
    }

    [Fact]
    public async Task ExecuteAsync_PerUserCap_DoesNotBlockDifferentUser()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        ConfigureCaps(maxGlobal: 100, maxPerUser: 1);
        var wf = CreateWorkflow();
        _seedDb.Workflows.Add(wf);
        await _seedDb.SaveChangesAsync();

        var runA = NewEngine().ExecuteAsync(wf, "test", CancellationToken.None, startedByUserId: userA);
        await WaitForInFlightAsync(1);

        // userB is allowed to start even though userA is capped — the per-user counter is
        // tracked separately for each user.
        var runB = NewEngine().ExecuteAsync(wf, "test", CancellationToken.None, startedByUserId: userB);
        await WaitForInFlightAsync(2);

        _gate.Release(2);
        await Task.WhenAll(runA, runB);
    }

    [Fact]
    public async Task ExecuteAsync_SubWorkflow_NotCountedInPerUserCap()
    {
        var userA = Guid.NewGuid();
        ConfigureCaps(maxGlobal: 100, maxPerUser: 1);
        var wf = CreateWorkflow();
        _seedDb.Workflows.Add(wf);
        await _seedDb.SaveChangesAsync();

        var parent = NewEngine().ExecuteAsync(wf, "test", CancellationToken.None,
            startedByUserId: userA, callDepth: 0);
        await WaitForInFlightAsync(1);

        // callDepth>0 means this is a sub-workflow call, which does not count against the
        // per-user cap. If it did count, the second call below would abort with
        // ExecutionCapacityException.
        var child = NewEngine().ExecuteAsync(wf, "test", CancellationToken.None,
            startedByUserId: userA, callDepth: 1);
        await WaitForInFlightAsync(2);

        _gate.Release(2);
        await Task.WhenAll(parent, child);
    }

    [Fact]
    public async Task ExecuteAsync_FinishedExecution_ReleasesPerUserSlot()
    {
        var userA = Guid.NewGuid();
        ConfigureCaps(maxGlobal: 100, maxPerUser: 1);
        var wf = CreateWorkflow();
        _seedDb.Workflows.Add(wf);
        await _seedDb.SaveChangesAsync();

        var run1 = NewEngine().ExecuteAsync(wf, "test", CancellationToken.None, startedByUserId: userA);
        await WaitForInFlightAsync(1);
        _gate.Release(1);
        await run1;

        // Once run1 finishes, its slot must be freed up again.
        var run2 = NewEngine().ExecuteAsync(wf, "test", CancellationToken.None, startedByUserId: userA);
        await WaitForInFlightAsync(2);
        _gate.Release(1);
        await run2;
    }

    [Fact]
    public async Task ExecuteAsync_CapZero_DisablesCheck()
    {
        // A max value of 0 or below disables the cap entirely. Operators who deliberately
        // want unlimited concurrency set it to 0 or a negative number.
        ConfigureCaps(maxGlobal: 0, maxPerUser: 0);
        var wf = CreateWorkflow();
        _seedDb.Workflows.Add(wf);
        await _seedDb.SaveChangesAsync();

        var runs = Enumerable.Range(0, 5)
            .Select(_ => NewEngine().ExecuteAsync(wf, "test", CancellationToken.None))
            .ToList();
        await WaitForInFlightAsync(5);

        _gate.Release(5);
        await Task.WhenAll(runs);
    }
}
