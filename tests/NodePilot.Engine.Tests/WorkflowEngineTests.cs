using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests;

[Collection("SerialEngineTests")]
public class WorkflowEngineTests
{
    private readonly NodePilotDbContext _db;
    private readonly IServiceProvider _serviceProvider;
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly Mock<IActivityExecutor> _mockExecutor;
    private readonly Mock<IActivityExecutor> _manualTriggerExecutor;
    private readonly ActivityRegistry _registry;
    private readonly WorkflowEngine _engine;

    public WorkflowEngineTests()
    {
        _mockExecutor = new Mock<IActivityExecutor>();
        _mockExecutor.Setup(e => e.ActivityType).Returns("runScript");
        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult { Success = true, Output = "OK" });

        // Roots are trigger-only, so every runnable fixture is rooted at a manualTrigger. The
        // trigger executes like any node (it produces one Succeeded step row).
        _manualTriggerExecutor = new Mock<IActivityExecutor>();
        _manualTriggerExecutor.Setup(e => e.ActivityType).Returns("manualTrigger");
        _manualTriggerExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult { Success = true, Output = "{}" });

        _registry = new ActivityRegistry(new[] { _mockExecutor.Object, _manualTriggerExecutor.Object });
        (_db, var sp, _connection) = TestDbContext.CreateWithScopedServices(_registry);
        _serviceProvider = sp;

        var logger = NullLogger<WorkflowEngine>.Instance;
        var notifier = new Mock<IExecutionNotifier>();
        _engine = new WorkflowEngine(_db, _registry, logger, _serviceProvider, notifier.Object);
    }

    private static Workflow CreateWorkflow(string definitionJson) => new Workflow
    {
        Id = Guid.NewGuid(),
        Name = "Test",
        DefinitionJson = definitionJson
    };

    // Roots are trigger-only: every runnable fixture starts at this manualTrigger, wired to the
    // former root node(s). The trigger itself runs and produces one extra Succeeded step row.
    private const string TriggerNodeJson =
        "{\"id\":\"trigger-1\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"activityType\":\"manualTrigger\",\"config\":{}}}";

    private static string BuildSingleNodeWorkflow(string nodeId = "step-1", string activityType = "runScript")
    {
        return "{\"nodes\":[" + TriggerNodeJson + ",{\"id\":\"" + nodeId + "\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"activityType\":\"" + activityType + "\",\"config\":{}}}],\"edges\":[{\"id\":\"te\",\"source\":\"trigger-1\",\"target\":\"" + nodeId + "\"}]}";
    }

    private static string BuildLinearWorkflow()
    {
        return "{\"nodes\":[" + TriggerNodeJson + ",{\"id\":\"step-1\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"activityType\":\"runScript\",\"config\":{}}},{\"id\":\"step-2\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"activityType\":\"runScript\",\"config\":{}}},{\"id\":\"step-3\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"activityType\":\"runScript\",\"config\":{}}}],\"edges\":[{\"id\":\"te\",\"source\":\"trigger-1\",\"target\":\"step-1\"},{\"id\":\"e1\",\"source\":\"step-1\",\"target\":\"step-2\"},{\"id\":\"e2\",\"source\":\"step-2\",\"target\":\"step-3\"}]}";
    }

    private static string BuildTwoParallelNodesWorkflow()
    {
        return "{\"nodes\":[" + TriggerNodeJson + ",{\"id\":\"step-1\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"activityType\":\"runScript\",\"config\":{}}},{\"id\":\"step-2\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"activityType\":\"runScript\",\"config\":{}}}],\"edges\":[{\"id\":\"te1\",\"source\":\"trigger-1\",\"target\":\"step-1\"},{\"id\":\"te2\",\"source\":\"trigger-1\",\"target\":\"step-2\"}]}";
    }

    private static string BuildDisabledEdgeWorkflow()
    {
        return "{\"nodes\":[" + TriggerNodeJson + ",{\"id\":\"step-1\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"activityType\":\"runScript\",\"config\":{}}},{\"id\":\"step-2\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"activityType\":\"runScript\",\"config\":{}}}],\"edges\":[{\"id\":\"te\",\"source\":\"trigger-1\",\"target\":\"step-1\"},{\"id\":\"e1\",\"source\":\"step-1\",\"target\":\"step-2\",\"data\":{\"disabled\":true}}]}";
    }

    /// <summary>
    /// trigger -> step-1 (enabled) -> step-2 (disabled, with regular incoming edge) -> step-3 (enabled).
    /// Regression for the disabled-target bug: previously only outgoing edges from disabled
    /// nodes were dropped, so step-2 was scheduled as step-1's successor and executed. The
    /// fix also drops edges whose target is disabled, making step-2 unreachable.
    /// </summary>
    private static string BuildDisabledTargetNodeWorkflow()
    {
        return "{\"nodes\":[" + TriggerNodeJson + ",{\"id\":\"step-1\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"activityType\":\"runScript\",\"config\":{}}},{\"id\":\"step-2\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"activityType\":\"runScript\",\"disabled\":true,\"config\":{}}},{\"id\":\"step-3\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"activityType\":\"runScript\",\"config\":{}}}],\"edges\":[{\"id\":\"te\",\"source\":\"trigger-1\",\"target\":\"step-1\"},{\"id\":\"e1\",\"source\":\"step-1\",\"target\":\"step-2\"},{\"id\":\"e2\",\"source\":\"step-2\",\"target\":\"step-3\"}]}";
    }

    private static string BuildConditionalEdgeWorkflow(string condition)
    {
        return "{\"nodes\":[" + TriggerNodeJson + ",{\"id\":\"step-1\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"activityType\":\"runScript\",\"config\":{}}},{\"id\":\"step-2\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"activityType\":\"runScript\",\"config\":{}}}],\"edges\":[{\"id\":\"te\",\"source\":\"trigger-1\",\"target\":\"step-1\"},{\"id\":\"e1\",\"source\":\"step-1\",\"target\":\"step-2\",\"data\":{\"condition\":\"" + condition + "\"}}]}";
    }

    [Fact]
    public async Task ExecuteAsync_SingleNode_ExecutesAndCompletes()
    {
        var workflow = CreateWorkflow(BuildSingleNodeWorkflow());
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Succeeded);
        execution.CompletedAt.Should().NotBeNull();

        var steps = _db.StepExecutions.Where(s => s.WorkflowExecutionId == execution.Id).ToList();
        steps.Should().HaveCount(2); // trigger-1 + step-1
        steps.Single(s => s.StepId == "step-1").Status.Should().Be(ExecutionStatus.Succeeded);
    }

    /// <summary>
    /// The "test step with context" feature relies on this: when a step produces OutputParameters, the
    /// engine must persist them as JSON so future test-context lookups can replay them.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_OutputParameters_PersistedAsJson()
    {
        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult
            {
                Success = true, Output = "OK",
                OutputParameters = new() { ["freeGb"] = "7", ["host"] = "srv01" }
            });

        var workflow = CreateWorkflow(BuildSingleNodeWorkflow());
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "u", CancellationToken.None);
        var step = _db.StepExecutions.Single(s => s.WorkflowExecutionId == execution.Id && s.StepId == "step-1");

        step.OutputParametersJson.Should().NotBeNullOrEmpty();
        using var doc = JsonDocument.Parse(step.OutputParametersJson!);
        doc.RootElement.GetProperty("freeGb").GetString().Should().Be("7");
        doc.RootElement.GetProperty("host").GetString().Should().Be("srv01");
    }

    [Fact]
    public async Task ExecuteAsync_SensitiveOutputParameterName_IsRedactedBeforePersistence()
    {
        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult
            {
                Success = true,
                OutputParameters = new()
                {
                    ["dbPassword"] = "opaque-secret-value",
                    ["promptTokens"] = "42",
                }
            });

        var workflow = CreateWorkflow(BuildSingleNodeWorkflow());
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "u", CancellationToken.None);
        var step = _db.StepExecutions.Single(row =>
            row.WorkflowExecutionId == execution.Id && row.StepId == "step-1");

        using var document = JsonDocument.Parse(step.OutputParametersJson!);
        document.RootElement.GetProperty("dbPassword").GetString().Should().Be("***");
        document.RootElement.GetProperty("promptTokens").GetString().Should().Be("42");
        step.OutputParametersJson.Should().NotContain("opaque-secret-value");
    }

    [Fact]
    public async Task ExecuteAsync_NoOutputParameters_PersistsNullJson()
    {
        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult { Success = true, Output = "OK" });

        var workflow = CreateWorkflow(BuildSingleNodeWorkflow());
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "u", CancellationToken.None);
        var step = _db.StepExecutions.Single(s => s.WorkflowExecutionId == execution.Id && s.StepId == "step-1");

        // Empty params dict must round-trip to null in the DB so we don't waste row width on
        // 80%+ of activities (delay/log/junction) that produce no params.
        step.OutputParametersJson.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WithExecutionIdOverride_ReusesPendingRow()
    {
        var workflow = CreateWorkflow(BuildSingleNodeWorkflow());
        _db.Workflows.Add(workflow);
        var pendingId = Guid.NewGuid();
        _db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = pendingId,
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Pending,
            StartedAt = DateTime.UtcNow,
            TriggeredBy = "manual",
        });
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(
            workflow,
            "manual",
            CancellationToken.None,
            executionIdOverride: pendingId);

        execution.Id.Should().Be(pendingId);
        execution.Status.Should().Be(ExecutionStatus.Succeeded);
        _db.WorkflowExecutions.Count(e => e.WorkflowId == workflow.Id).Should().Be(1);
        var row = await _db.WorkflowExecutions.FindAsync(pendingId);
        row!.Status.Should().Be(ExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task ExecuteAsync_WithExecutionIdOverride_DoesNotReviveCancelledPendingRow()
    {
        var workflow = CreateWorkflow(BuildSingleNodeWorkflow());
        _db.Workflows.Add(workflow);
        var executionId = Guid.NewGuid();
        _db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Cancelled,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            TriggeredBy = "manual",
        });
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(
            workflow,
            "manual",
            CancellationToken.None,
            executionIdOverride: executionId);

        execution.Id.Should().Be(executionId);
        execution.Status.Should().Be(ExecutionStatus.Cancelled);
        _db.StepExecutions.Count(s => s.WorkflowExecutionId == executionId).Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_WithStaleTrackedPending_DoesNotOverwriteConcurrentOffboardingCancellation()
    {
        var workflow = CreateWorkflow(BuildSingleNodeWorkflow());
        var executionId = Guid.NewGuid();
        var trackedPending = new WorkflowExecution
        {
            Id = executionId,
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Pending,
            StartedAt = DateTime.UtcNow,
            TriggeredBy = "scheduleTrigger",
        };
        _db.AddRange(workflow, trackedPending);
        await _db.SaveChangesAsync();

        // Simulate AD/SCIM/admin offboarding through a different request scope. The
        // engine context deliberately keeps its stale Pending entity tracked; the old
        // read-modify-save transition revived this row and executed it.
        var concurrentOptions = new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(_connection)
            .Options;
        await using (var offboardingDb = new NodePilotDbContext(concurrentOptions))
        {
            await offboardingDb.WorkflowExecutions
                .Where(candidate => candidate.Id == executionId
                                 && candidate.Status == ExecutionStatus.Pending)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(candidate => candidate.Status, ExecutionStatus.Cancelled)
                    .SetProperty(candidate => candidate.CancelledBy, "directory-offboarding")
                    .SetProperty(candidate => candidate.CompletedAt, DateTime.UtcNow));
        }
        trackedPending.Status.Should().Be(ExecutionStatus.Pending,
            "the regression requires a stale tracked Pending entity");

        var execution = await _engine.ExecuteAsync(
            workflow,
            "scheduleTrigger",
            CancellationToken.None,
            executionIdOverride: executionId);

        execution.Status.Should().Be(ExecutionStatus.Cancelled);
        execution.CancelledBy.Should().Be("directory-offboarding");
        _db.StepExecutions.Count(candidate => candidate.WorkflowExecutionId == executionId)
            .Should().Be(0);
        _manualTriggerExecutor.Verify(candidate => candidate.ExecuteAsync(
            It.IsAny<StepExecutionContext>(),
            It.IsAny<JsonElement>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _mockExecutor.Verify(candidate => candidate.ExecuteAsync(
            It.IsAny<StepExecutionContext>(),
            It.IsAny<JsonElement>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_TerminalWriteback_DoesNotOverwriteConcurrentOffboardingCancellation(
        bool stepSucceeds)
    {
        var stepStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                stepStarted.TrySetResult();
                await releaseStep.Task;
                return stepSucceeds
                    ? new ActivityResult { Success = true, Output = "OK" }
                    : new ActivityResult { Success = false, ErrorOutput = "failed after offboarding" };
            });

        var workflow = CreateWorkflow(BuildSingleNodeWorkflow());
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var executionTask = _engine.ExecuteAsync(workflow, "scheduleTrigger", CancellationToken.None);
        await stepStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var executionId = await _db.WorkflowExecutions.AsNoTracking()
            .Where(candidate => candidate.WorkflowId == workflow.Id)
            .Select(candidate => candidate.Id)
            .SingleAsync();
        await using (var offboardingDb = new NodePilotDbContext(
                         new DbContextOptionsBuilder<NodePilotDbContext>()
                             .UseSqlite(_connection).Options))
        {
            var updated = await offboardingDb.WorkflowExecutions
                .Where(candidate => candidate.Id == executionId
                                 && candidate.Status == ExecutionStatus.Running)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(candidate => candidate.Status, ExecutionStatus.Cancelled)
                    .SetProperty(candidate => candidate.CancelledBy, "directory-offboarding")
                    .SetProperty(candidate => candidate.CompletedAt, DateTime.UtcNow));
            updated.Should().Be(1);
        }

        releaseStep.TrySetResult();
        var execution = await executionTask.WaitAsync(TimeSpan.FromSeconds(5));

        execution.Status.Should().Be(ExecutionStatus.Cancelled);
        execution.CancelledBy.Should().Be("directory-offboarding");
        _db.ChangeTracker.Clear();
        var persisted = await _db.WorkflowExecutions.FindAsync(executionId);
        persisted!.Status.Should().Be(ExecutionStatus.Cancelled);
        persisted.CancelledBy.Should().Be("directory-offboarding");
    }

    [Fact]
    public async Task ExecuteAsync_TerminalSuccess_IsFencedByDatabaseLeaseEpochAfterGcLikePause()
    {
        var stepStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                stepStarted.TrySetResult();
                await releaseStep.Task;
                return new ActivityResult { Success = true, Output = "OK" };
            });

        var cluster = new StaleClusterState("node-a", leaseEpoch: 7);
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(options => options.UseSqlite(_connection));
        services.AddScoped(_ => _registry);
        services.AddSingleton<IClusterStateProvider>(cluster);
        await using var serviceProvider = services.BuildServiceProvider();
        var engine = new WorkflowEngine(
            _db,
            _registry,
            NullLogger<WorkflowEngine>.Instance,
            serviceProvider,
            new Mock<IExecutionNotifier>().Object);

        var now = DateTime.UtcNow;
        _db.ClusterLeaders.Add(new ClusterLeader
        {
            Resource = "primary",
            OwnerNodeId = "node-a",
            LeaseEpoch = 7,
            AcquiredAt = now,
            LastRenewedAt = now,
            ExpiresAt = now.AddMinutes(1),
        });
        var workflow = CreateWorkflow(BuildSingleNodeWorkflow());
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var executionTask = engine.ExecuteAsync(workflow, "scheduleTrigger", CancellationToken.None);
        await stepStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var executionId = await _db.WorkflowExecutions.AsNoTracking()
            .Where(candidate => candidate.WorkflowId == workflow.Id)
            .Select(candidate => candidate.Id)
            .SingleAsync();

        // Simulate a stop-the-world pause on node-a while node-b acquires a newer lease.
        // The fake provider intentionally remains stale to prove the database predicate,
        // rather than an in-memory IsLeader check, fences the terminal write.
        await using (var failoverDb = new NodePilotDbContext(
                         new DbContextOptionsBuilder<NodePilotDbContext>()
                             .UseSqlite(_connection).Options))
        {
            await failoverDb.ClusterLeaders
                .Where(candidate => candidate.Resource == "primary")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(candidate => candidate.OwnerNodeId, "node-b")
                    .SetProperty(candidate => candidate.LeaseEpoch, 8L)
                    .SetProperty(candidate => candidate.AcquiredAt, DateTime.UtcNow)
                    .SetProperty(candidate => candidate.LastRenewedAt, DateTime.UtcNow)
                    .SetProperty(candidate => candidate.ExpiresAt, DateTime.UtcNow.AddMinutes(1)));
        }

        releaseStep.TrySetResult();
        var staleResult = await executionTask.WaitAsync(TimeSpan.FromSeconds(5));

        staleResult.Status.Should().Be(ExecutionStatus.Running,
            "the old leader must leave terminalization to failover recovery");
        _db.ChangeTracker.Clear();
        (await _db.WorkflowExecutions.FindAsync(executionId))!.Status
            .Should().Be(ExecutionStatus.Running);

        var recovered = await NodePilot.Engine.Execution.StartupRecovery.RecoverOrphanedExecutionsAsync(
            _db, NullLogger.Instance, ourNodeId: "node-b", leaseEpoch: 8);
        recovered.Should().Be(1);
        (await _db.WorkflowExecutions.FindAsync(executionId))!.Status
            .Should().Be(ExecutionStatus.Cancelled);
    }

    [Fact]
    public async Task ExecuteAsync_TerminalSuccess_IsFencedByDatabaseExpiredLease()
    {
        var cluster = new StaleClusterState("node-a", leaseEpoch: 11);
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(options => options.UseSqlite(_connection));
        services.AddScoped(_ => _registry);
        services.AddSingleton<IClusterStateProvider>(cluster);
        await using var serviceProvider = services.BuildServiceProvider();
        var engine = new WorkflowEngine(
            _db,
            _registry,
            NullLogger<WorkflowEngine>.Instance,
            serviceProvider,
            new Mock<IExecutionNotifier>().Object);

        var now = DateTime.UtcNow;
        _db.ClusterLeaders.Add(new ClusterLeader
        {
            Resource = "primary",
            OwnerNodeId = "node-a",
            LeaseEpoch = 11,
            AcquiredAt = now.AddMinutes(-2),
            LastRenewedAt = now.AddMinutes(-1),
            ExpiresAt = now.AddSeconds(-10),
        });
        var workflow = CreateWorkflow(BuildSingleNodeWorkflow());
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var result = await engine.ExecuteAsync(
            workflow, "scheduleTrigger", CancellationToken.None);

        result.Status.Should().Be(ExecutionStatus.Running,
            "an expired database lease must reject terminal writeback even if local state still says leader");
        _db.ChangeTracker.Clear();
        (await _db.WorkflowExecutions.FindAsync(result.Id))!.Status
            .Should().Be(ExecutionStatus.Running);
    }

    [Fact]
    public async Task ExecuteAsync_LinearChain_ExecutesInOrder()
    {
        var executionOrder = new List<string>();
        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .Callback<StepExecutionContext, JsonElement, CancellationToken>((ctx, _, _) => executionOrder.Add(ctx.StepId))
            .ReturnsAsync(new ActivityResult { Success = true, Output = "OK" });

        var workflow = CreateWorkflow(BuildLinearWorkflow());
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Succeeded);
        executionOrder.Should().ContainInOrder("step-1", "step-2", "step-3");
        executionOrder.Should().HaveCount(3);
    }

    [Fact]
    public async Task ExecuteAsync_ParallelBranches_ExecutesConcurrently()
    {
        var executedSteps = new List<string>();
        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .Callback<StepExecutionContext, JsonElement, CancellationToken>((ctx, _, _) =>
            {
                lock (executedSteps) { executedSteps.Add(ctx.StepId); }
            })
            .ReturnsAsync(new ActivityResult { Success = true, Output = "OK" });

        var workflow = CreateWorkflow(BuildTwoParallelNodesWorkflow());
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Succeeded);
        executedSteps.Should().HaveCount(2);
        executedSteps.Should().Contain("step-1");
        executedSteps.Should().Contain("step-2");
    }

    [Fact]
    public async Task ExecuteAsync_DisabledEdge_SkipsDisabledPath()
    {
        var executedSteps = new List<string>();
        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .Callback<StepExecutionContext, JsonElement, CancellationToken>((ctx, _, _) => executedSteps.Add(ctx.StepId))
            .ReturnsAsync(new ActivityResult { Success = true, Output = "OK" });

        var workflow = CreateWorkflow(BuildDisabledEdgeWorkflow());
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Succeeded);
        // Semantic: a disabled edge is treated as "path not taken". step-2 is the ONLY target
        // of the (disabled) edge, so it's unreachable and must be Skipped, NOT promoted to root.
        executedSteps.Should().Contain("step-1");
        executedSteps.Should().NotContain("step-2");
        var step2 = _db.StepExecutions.FirstOrDefault(s => s.StepId == "step-2" && s.WorkflowExecutionId == execution.Id);
        step2.Should().NotBeNull();
        step2!.Status.Should().Be(ExecutionStatus.Skipped);
    }

    [Fact]
    public async Task ExecuteAsync_DisabledNode_IsNotExecutedEvenWithEnabledIncomingEdge()
    {
        // Regression for the bug where disabled nodes ran whenever an enabled predecessor
        // had a normal (non-disabled) edge pointing to them. The fix drops edges whose
        // target is disabled, making the disabled node unreachable in the active graph.
        var executedSteps = new List<string>();
        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .Callback<StepExecutionContext, JsonElement, CancellationToken>((ctx, _, _) => executedSteps.Add(ctx.StepId))
            .ReturnsAsync(new ActivityResult { Success = true, Output = "OK" });

        var workflow = CreateWorkflow(BuildDisabledTargetNodeWorkflow());
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Succeeded);
        executedSteps.Should().Contain("step-1");
        executedSteps.Should().NotContain("step-2"); // disabled — must never execute
        executedSteps.Should().NotContain("step-3"); // unreachable through skipped step-2

        var step2 = _db.StepExecutions.FirstOrDefault(s => s.StepId == "step-2" && s.WorkflowExecutionId == execution.Id);
        step2.Should().NotBeNull();
        step2!.Status.Should().Be(ExecutionStatus.Skipped);

        var step3 = _db.StepExecutions.FirstOrDefault(s => s.StepId == "step-3" && s.WorkflowExecutionId == execution.Id);
        step3.Should().NotBeNull();
        step3!.Status.Should().Be(ExecutionStatus.Skipped);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessCondition_FollowsPathWhenStepSucceeds()
    {
        var executedSteps = new List<string>();
        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .Callback<StepExecutionContext, JsonElement, CancellationToken>((ctx, _, _) => executedSteps.Add(ctx.StepId))
            .ReturnsAsync(new ActivityResult { Success = true, Output = "OK" });

        var workflow = CreateWorkflow(BuildConditionalEdgeWorkflow("step-1.success"));
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        executedSteps.Should().Contain("step-1");
        executedSteps.Should().Contain("step-2");
    }

    [Fact]
    public async Task ExecuteAsync_SuccessCondition_SkipsPathWhenStepFails()
    {
        var executedSteps = new List<string>();
        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .Callback<StepExecutionContext, JsonElement, CancellationToken>((ctx, _, _) => executedSteps.Add(ctx.StepId))
            .ReturnsAsync((StepExecutionContext ctx, JsonElement _, CancellationToken _) =>
                ctx.StepId == "step-1"
                    ? new ActivityResult { Success = false, ErrorOutput = "fail" }
                    : new ActivityResult { Success = true, Output = "OK" });

        var workflow = CreateWorkflow(BuildConditionalEdgeWorkflow("step-1.success"));
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        executedSteps.Should().Contain("step-1");
        executedSteps.Should().NotContain("step-2");
    }

    [Fact]
    public async Task ExecuteAsync_FailedCondition_FollowsPathWhenStepFails()
    {
        var executedSteps = new List<string>();
        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .Callback<StepExecutionContext, JsonElement, CancellationToken>((ctx, _, _) => executedSteps.Add(ctx.StepId))
            .ReturnsAsync((StepExecutionContext ctx, JsonElement _, CancellationToken _) =>
                ctx.StepId == "step-1"
                    ? new ActivityResult { Success = false, ErrorOutput = "fail" }
                    : new ActivityResult { Success = true, Output = "OK" });

        var workflow = CreateWorkflow(BuildConditionalEdgeWorkflow("step-1.failed"));
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        executedSteps.Should().Contain("step-1");
        executedSteps.Should().Contain("step-2");
    }

    [Fact]
    public async Task ExecuteAsync_StepFails_SetsExecutionStatusFailed()
    {
        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult { Success = false, ErrorOutput = "Script failed" });

        var workflow = CreateWorkflow(BuildSingleNodeWorkflow());
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Failed);
        execution.CompletedAt.Should().NotBeNull();
        execution.ErrorMessage.Should().Be("Activity \"step-1\" failed: Script failed");

        var steps = _db.StepExecutions.Where(s => s.WorkflowExecutionId == execution.Id).ToList();
        steps.Should().HaveCount(2); // trigger-1 + step-1
        var failedStep = steps.Single(s => s.StepId == "step-1");
        failedStep.Status.Should().Be(ExecutionStatus.Failed);
        failedStep.ErrorOutput.Should().Be("Script failed");
    }

    [Fact]
    public async Task ExecuteAsync_MultipleStepsFail_SummarizesFirstFailureAndAdditionalCount()
    {
        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((StepExecutionContext ctx, JsonElement _, CancellationToken _) =>
                new ActivityResult { Success = false, ErrorOutput = $"{ctx.StepId} failed" });

        const string definition = """
            {
              "nodes": [
                {"id":"trigger-1","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"manualTrigger","config":{}}},
                {"id":"step-1","type":"activity","position":{"x":0,"y":0},"data":{"label":"Primary activity","activityType":"runScript","config":{}}},
                {"id":"step-2","type":"activity","position":{"x":0,"y":0},"data":{"label":"Secondary activity","activityType":"runScript","config":{}}}
              ],
              "edges": [
                {"id":"te","source":"trigger-1","target":"step-1"},
                {"id":"e1","source":"step-1","target":"step-2","data":{"condition":"step-1.failed"}}
              ]
            }
            """;
        var workflow = CreateWorkflow(definition);
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Failed);
        execution.ErrorMessage.Should().Be(
            "Activity \"Primary activity\" failed: step-1 failed (+1 more failed activities)");
    }

    [Fact]
    public async Task ExecuteAsync_StepFailsWithoutErrorOutput_OmitsEmptyDetail()
    {
        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult { Success = false });

        var workflow = CreateWorkflow(BuildSingleNodeWorkflow("unnamed-step"));
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Failed);
        execution.ErrorMessage.Should().Be("Activity \"unnamed-step\" failed");
    }

    [Fact]
    public async Task ExecuteAsync_AllStepsSucceed_SetsStatusSucceeded()
    {
        var workflow = CreateWorkflow(BuildLinearWorkflow());
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Succeeded);
        execution.CompletedAt.Should().NotBeNull();
        execution.ErrorMessage.Should().BeNull();

        var steps = _db.StepExecutions.Where(s => s.WorkflowExecutionId == execution.Id).ToList();
        steps.Should().HaveCount(4); // step-1/2/3 + trigger-1
        steps.Should().AllSatisfy(s => s.Status.Should().Be(ExecutionStatus.Succeeded));
    }

    [Fact]
    public async Task ExecuteAsync_Cancellation_SetsStatusCancelled()
    {
        using var cts = new CancellationTokenSource();

        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .Callback<StepExecutionContext, JsonElement, CancellationToken>((_, _, _) => cts.Cancel())
            .ThrowsAsync(new OperationCanceledException());

        var workflow = CreateWorkflow(BuildSingleNodeWorkflow());
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "test-user", cts.Token);

        execution.Status.Should().Be(ExecutionStatus.Cancelled);
        execution.CompletedAt.Should().NotBeNull();
        execution.CancelledBy.Should().Be("system", "a bare token/timeout cancel with no recorded reason attributes to 'system', not 'user'");
    }

    [Fact]
    public async Task ExecuteAsync_ManualCancelViaCancelAsync_AttributesCancelledByUser()
    {
        // Reproduces the live manual-cancel path: the controller calls CancelAsync(id, "user"), which
        // records the reason before tripping the token — the engine's OCE catch (on its own thread) then
        // stamps CancelledBy="user". This is the load-bearing wiring for the "manual cancel" alert.
        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .Callback<StepExecutionContext, JsonElement, CancellationToken>(
                (ctx, _, _) => _engine.CancelAsync(ctx.WorkflowExecutionId, "user").GetAwaiter().GetResult())
            .ThrowsAsync(new OperationCanceledException());

        var workflow = CreateWorkflow(BuildSingleNodeWorkflow());
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Cancelled);
        execution.CancelledBy.Should().Be("user", "CancelAsync(id, \"user\") records the reason the engine catch reads back");
    }

    [Fact]
    public async Task CancelAsync_UnknownExecution_ReturnsFalse()
    {
        var randomId = Guid.NewGuid();

        var result = await _engine.CancelAsync(randomId);

        result.Should().BeFalse("no in-memory token exists for an unknown execution id");
    }

    [Fact]
    public async Task CancelAllLocalAsync_NoRunning_ReturnsZero()
    {
        // Sanity check: with no in-flight runs (we just constructed the engine), the
        // fencing call must be a no-op rather than throwing or returning a stale count.
        var count = await NodePilot.Engine.WorkflowEngine.CancelAllLocalAsync();
        count.Should().Be(0);
    }

    [Fact]
    public async Task CancelAllLocalAsync_TriggersAllInflightCts()
    {
        // Reach into the static dict to simulate two concurrent runs without spinning up
        // real executions. This is the exact contract the cluster fencing host depends on:
        // CancelAllLocalAsync must trip every CTS in _runningExecutions, regardless of
        // which engine instance owns them.
        var dictField = typeof(NodePilot.Engine.WorkflowEngine).GetField(
            "_runningExecutions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var dict = (System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource>)
            dictField.GetValue(null)!;

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var cts1 = new CancellationTokenSource();
        var cts2 = new CancellationTokenSource();
        dict[id1] = cts1;
        dict[id2] = cts2;

        try
        {
            var count = await NodePilot.Engine.WorkflowEngine.CancelAllLocalAsync();
            count.Should().Be(2);
            cts1.IsCancellationRequested.Should().BeTrue();
            cts2.IsCancellationRequested.Should().BeTrue();
        }
        finally
        {
            dict.TryRemove(id1, out _);
            dict.TryRemove(id2, out _);
            cts1.Dispose();
            cts2.Dispose();
        }
    }

    [Fact]
    public async Task RecoverOrphanedExecutionsAsync_CleansUpRunningRows()
    {
        // Seed fake orphans: Running execution + Running step, plus a queued Pending execution.
        var workflow = CreateWorkflow(BuildSingleNodeWorkflow());
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();
        var orphanId = Guid.NewGuid();
        var pendingId = Guid.NewGuid();
        _db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = orphanId,
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Running,
            StartedAt = DateTime.UtcNow.AddHours(-5),
            TriggeredBy = "test",
        });
        _db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = pendingId,
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Pending,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            TriggeredBy = "manual",
        });
        _db.StepExecutions.Add(new StepExecution
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = orphanId,
            StepId = "step-1",
            StepName = "orphaned step",
            StepType = "RunScript",
            Status = ExecutionStatus.Running,
            StartedAt = DateTime.UtcNow.AddHours(-5),
        });
        await _db.SaveChangesAsync();

        var recovered = await NodePilot.Engine.Execution.StartupRecovery.RecoverOrphanedExecutionsAsync(
            _db, NullLogger<WorkflowEngine>.Instance);

        recovered.Should().Be(2);

        var exec = await _db.WorkflowExecutions.FindAsync(orphanId);
        exec!.Status.Should().Be(ExecutionStatus.Cancelled);
        exec.CompletedAt.Should().NotBeNull();
        exec.ErrorMessage.Should().Contain("orphaned");

        var pending = await _db.WorkflowExecutions.FindAsync(pendingId);
        pending!.Status.Should().Be(ExecutionStatus.Cancelled);
        pending.CompletedAt.Should().NotBeNull();
        pending.ErrorMessage.Should().Contain("queued");

        var steps = _db.StepExecutions.Where(s => s.WorkflowExecutionId == orphanId).ToList();
        steps.Should().OnlyContain(s => s.Status == ExecutionStatus.Cancelled);
    }

    /* ------------- Bug 1: cascade-skip must respect live paths ------------- */

    /// <summary>
    /// When edge A→C has a failing condition, but C is also reachable via B (which succeeds),
    /// C must still execute. Previously the over-eager MarkSubtreeSkipped also skipped C.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_FailedConditionToJunction_DoesNotSkipDownstreamReachableViaSibling()
    {
        var executedSteps = new List<string>();
        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .Callback<StepExecutionContext, JsonElement, CancellationToken>((ctx, _, _) =>
            {
                lock (executedSteps) { executedSteps.Add(ctx.StepId); }
            })
            .ReturnsAsync((StepExecutionContext ctx, JsonElement _, CancellationToken _) =>
                ctx.StepId == "src"
                    ? new ActivityResult { Success = false, ErrorOutput = "fail" }
                    : new ActivityResult { Success = true, Output = "OK" });

        // Workflow: src → branchA (only on success — will be skipped)
        //           src → branchB (always — will run)
        //           both → finalStep (no junction, plain merge)
        var def = """
        {
          "nodes":[
            {"id":"trigger-1","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"manualTrigger","config":{}}},
            {"id":"src","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{}}},
            {"id":"branchA","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{}}},
            {"id":"branchB","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{}}},
            {"id":"finalStep","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{}}}
          ],
          "edges":[
            {"id":"te","source":"trigger-1","target":"src"},
            {"id":"e1","source":"src","target":"branchA","data":{"condition":"src.success"}},
            {"id":"e2","source":"src","target":"branchB"},
            {"id":"e3","source":"branchA","target":"finalStep"},
            {"id":"e4","source":"branchB","target":"finalStep"}
          ]
        }
        """;
        var workflow = CreateWorkflow(def);
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        await _engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        executedSteps.Should().Contain("src");
        executedSteps.Should().Contain("branchB");
        executedSteps.Should().NotContain("branchA");
        // The fix: finalStep still runs because it has a live path via branchB,
        // even though branchA was condition-skipped.
        executedSteps.Should().Contain("finalStep");
    }

    /// <summary>
    /// When ALL paths to a downstream node are skipped, that node must in turn be skipped.
    /// Verifies the cascade still functions correctly for orphaned descendants.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_AllPathsConditionSkipped_DownstreamAlsoSkipped()
    {
        var executedSteps = new List<string>();
        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .Callback<StepExecutionContext, JsonElement, CancellationToken>((ctx, _, _) =>
            {
                lock (executedSteps) { executedSteps.Add(ctx.StepId); }
            })
            .ReturnsAsync((StepExecutionContext ctx, JsonElement _, CancellationToken _) =>
                ctx.StepId == "src"
                    ? new ActivityResult { Success = false, ErrorOutput = "fail" }
                    : new ActivityResult { Success = true, Output = "OK" });

        // Both incoming edges to finalStep require src.success. src fails → both skipped → finalStep skipped.
        var def = """
        {
          "nodes":[
            {"id":"trigger-1","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"manualTrigger","config":{}}},
            {"id":"src","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{}}},
            {"id":"branchA","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{}}},
            {"id":"branchB","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{}}},
            {"id":"finalStep","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{}}}
          ],
          "edges":[
            {"id":"te","source":"trigger-1","target":"src"},
            {"id":"e1","source":"src","target":"branchA","data":{"condition":"src.success"}},
            {"id":"e2","source":"src","target":"branchB","data":{"condition":"src.success"}},
            {"id":"e3","source":"branchA","target":"finalStep"},
            {"id":"e4","source":"branchB","target":"finalStep"}
          ]
        }
        """;
        var workflow = CreateWorkflow(def);
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        await _engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        executedSteps.Should().Contain("src");
        executedSteps.Should().NotContain("branchA");
        executedSteps.Should().NotContain("branchB");
        executedSteps.Should().NotContain("finalStep");
    }

    /* ------------- Bug 2: waitAny actually races (event-driven) ------------- */

    /// <summary>
    /// waitAny junction must fire as soon as the FIRST branch completes, not wait for the
    /// slowest sibling. Previously Task.WhenAll(batch) blocked the whole wave.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WaitAnyJunction_FiresAfterFirstBranchCompletes()
    {
        // Mock for runScript: branchFast=10ms, branchSlow=2000ms (DIFFERENCE the bug exposes)
        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .Returns<StepExecutionContext, JsonElement, CancellationToken>(async (ctx, _, ct) =>
            {
                var ms = ctx.StepId switch
                {
                    "branchFast" => 10,
                    "branchSlow" => 2000,
                    _ => 5,
                };
                await Task.Delay(ms, ct);
                return new ActivityResult { Success = true, Output = ctx.StepId };
            });

        // Junction executor (waitAny): just returns success
        var mockJunction = new Mock<IActivityExecutor>();
        mockJunction.Setup(e => e.ActivityType).Returns("junction");
        mockJunction.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult { Success = true, Output = "merged" });

        var registry = new ActivityRegistry(new[] { _mockExecutor.Object, mockJunction.Object });
        var sp = TestDbContext.BuildScopeProviderOnSameConnection(_connection, registry);
        var notifier = new Mock<IExecutionNotifier>();
        var engine = new WorkflowEngine(_db, registry, NullLogger<WorkflowEngine>.Instance,
            sp, notifier.Object);

        var def = """
        {
          "nodes":[
            {"id":"branchFast","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{}}},
            {"id":"branchSlow","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{}}},
            {"id":"join","type":"junction","position":{"x":0,"y":0},"data":{"activityType":"junction","config":{"mode":"waitAny"}}},
            {"id":"final","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{}}}
          ],
          "edges":[
            {"id":"e1","source":"branchFast","target":"join"},
            {"id":"e2","source":"branchSlow","target":"join"},
            {"id":"e3","source":"join","target":"final"}
          ]
        }
        """;
        var workflow = CreateWorkflow(def);
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);
        sw.Stop();

        // Must complete in significantly less than branchSlow's 2000ms — proves we didn't wait.
        sw.ElapsedMilliseconds.Should().BeLessThan(1500,
            "waitAny must fire after the fast branch (≈10ms), not after the slow branch (2000ms)");
    }

    [Fact]
    public async Task ExecuteAsync_WithParameters_PersistsInputParametersJson()
    {
        var workflow = CreateWorkflow(BuildSingleNodeWorkflow());
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var parameters = new Dictionary<string, string>
        {
            { "env", "prod" },
            { "version", "2.1.0" }
        };

        var execution = await _engine.ExecuteAsync(workflow, "manual", CancellationToken.None, parameters);

        execution.InputParametersJson.Should().NotBeNull();
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(execution.InputParametersJson!);
        parsed.Should().NotBeNull();
        parsed!["env"].Should().Be("prod");
        parsed["version"].Should().Be("2.1.0");
    }

    [Fact]
    public async Task ExecuteAsync_NoParameters_LeavesInputParametersJsonNull()
    {
        var workflow = CreateWorkflow(BuildSingleNodeWorkflow());
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "manual", CancellationToken.None);

        execution.InputParametersJson.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_RetryPolicy_RetriesUntilSuccess_RecordsAttemptCount()
    {
        int call = 0;
        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<System.Text.Json.JsonElement>(),
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                call++;
                return Task.FromResult(call < 3
                    ? new ActivityResult { Success = false, ErrorOutput = "transient" }
                    : new ActivityResult { Success = true, Output = "ok" });
            });

        var def = """{"nodes":[{"id":"trigger-1","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"manualTrigger","config":{}}},{"id":"s","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{"retry":{"maxAttempts":5,"backoff":"fixed","initialDelayMs":0}}}}],"edges":[{"id":"te","source":"trigger-1","target":"s"}]}""";
        var workflow = CreateWorkflow(def);
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "test", CancellationToken.None);

        call.Should().Be(3, "executor should be re-invoked until it returns success");
        execution.Status.Should().Be(ExecutionStatus.Succeeded);
        var step = await _db.StepExecutions.FirstOrDefaultAsync(s => s.WorkflowExecutionId == execution.Id && s.StepId == "s");
        step!.AttemptCount.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_RetryPolicy_ExhaustsAttempts_FailsWithAttemptCountAtMax()
    {
        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<System.Text.Json.JsonElement>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult { Success = false, ErrorOutput = "always broken" });

        var def = """{"nodes":[{"id":"trigger-1","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"manualTrigger","config":{}}},{"id":"s","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{"retry":{"maxAttempts":3,"backoff":"fixed","initialDelayMs":0}}}}],"edges":[{"id":"te","source":"trigger-1","target":"s"}]}""";
        var workflow = CreateWorkflow(def);
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "test", CancellationToken.None);
        execution.Status.Should().Be(ExecutionStatus.Failed);
        var step = await _db.StepExecutions.FirstOrDefaultAsync(s => s.WorkflowExecutionId == execution.Id && s.StepId == "s");
        step!.AttemptCount.Should().Be(3, "retry policy ran the full budget before giving up");
        step.Status.Should().Be(ExecutionStatus.Failed);
    }

    [Fact]
    public async Task ExecuteAsync_NoRetryConfig_RunsOnceEvenOnFailure()
    {
        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<System.Text.Json.JsonElement>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult { Success = false, ErrorOutput = "nope" });

        var workflow = CreateWorkflow(BuildSingleNodeWorkflow());
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "test", CancellationToken.None);
        var step = await _db.StepExecutions.FirstOrDefaultAsync(s => s.WorkflowExecutionId == execution.Id);
        step!.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutSecondsElapsed_CancelsRun()
    {
        // Executor that parks for 5 s — timeoutSeconds=1 must trip the linked CTS and make
        // the whole workflow abandon with a Cancelled terminal status.
        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<System.Text.Json.JsonElement>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (StepExecutionContext _, System.Text.Json.JsonElement __, CancellationToken ct) =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                catch (OperationCanceledException) { /* expected when timeout trips */ }
                return new ActivityResult { Success = false, ErrorOutput = "cancelled" };
            });

        var workflow = CreateWorkflow(BuildSingleNodeWorkflow());
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var execution = await _engine.ExecuteAsync(workflow, "test", CancellationToken.None,
            inputParameters: null, timeoutSeconds: 1);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(4000,
            "1-second execution timeout must trip well before the executor's natural 5-second delay");
        execution.Status.Should().BeOneOf(ExecutionStatus.Cancelled, ExecutionStatus.Failed);
    }

    [Fact]
    public async Task ExecuteAsync_OnlyReservedParameters_LeavesInputParametersJsonNull()
    {
        // __callDepth is a recursion-guard bookkeeping value from startWorkflow, not a
        // user-supplied parameter. Storing it would be misleading in post-mortem review.
        var workflow = CreateWorkflow(BuildSingleNodeWorkflow());
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var parameters = new Dictionary<string, string> { { "__callDepth", "3" } };

        var execution = await _engine.ExecuteAsync(workflow, "manual", CancellationToken.None, parameters);

        execution.InputParametersJson.Should().BeNull();
    }

    /* ------------- Bug: cycle-only graph must fail, not silently succeed ------------- */

    /// <summary>
    /// A workflow where every node has an incoming edge (pure cycle, no root) must be
    /// marked Failed with an actionable ErrorMessage. Previously the empty queue caused
    /// the engine to exit the scheduling loop immediately and return Succeeded with 0 steps.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CycleOnlyGraph_MarksExecutionFailed()
    {
        // step-1 → step-2 → step-1: every node has an incoming edge, no roots.
        const string cycleDefinition = """
            {
              "nodes": [
                {"id":"step-1","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{}}},
                {"id":"step-2","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{}}}
              ],
              "edges": [
                {"id":"e1","source":"step-1","target":"step-2"},
                {"id":"e2","source":"step-2","target":"step-1"}
              ]
            }
            """;

        var workflow = CreateWorkflow(cycleDefinition);
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Failed,
            "a cycle-only graph has no entry point and must not silently succeed");
        execution.ErrorMessage.Should().NotBeNullOrEmpty();
        execution.ErrorMessage.Should().NotStartWith("Activity ",
            "engine-level graph failures must keep their existing workflow error");
        execution.CompletedAt.Should().NotBeNull();

        // No steps should have been created — nothing could run.
        var steps = _db.StepExecutions.Where(s => s.WorkflowExecutionId == execution.Id).ToList();
        steps.Should().BeEmpty("no step can start when there are no root nodes");
    }

    /// <summary>
    /// An empty workflow (no nodes, no edges) has no root nodes either, but nodes.Count == 0
    /// so the cycle-detection guard must NOT fire — the empty workflow should still Succeed.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_EmptyWorkflow_SucceedsWithoutSteps()
    {
        const string emptyDefinition = """{"nodes":[],"edges":[]}""";

        var workflow = CreateWorkflow(emptyDefinition);
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Succeeded,
            "an empty workflow is valid — there is simply nothing to execute");
    }

    private sealed class StaleClusterState(string nodeId, long leaseEpoch) : IClusterStateProvider
    {
        public bool IsLeader => true;
        public string NodeId => nodeId;
        public DateTime? LeaseExpiresAt => DateTime.UtcNow.AddMinutes(1);
        public long LeaseEpoch => leaseEpoch;
        public DateTime? LastSuccessfulRenewAt => DateTime.UtcNow;
        public event Action<long>? OnLeadershipAcquired { add { } remove { } }
        public event Action? OnLeadershipLost { add { } remove { } }
    }
}
