using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Api.ExecutionDispatch;
using NodePilot.Core.Enums;
using NodePilot.Core.ExecutionDispatch;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Security;
using Xunit;

namespace NodePilot.Api.Tests.ExecutionDispatch;

public class ExecutionDispatchServiceTests
{
    [Fact]
    public async Task DispatchAsync_CreatesPendingAndDurableOutboxInSameSave()
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = EnabledWorkflow();
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        await using var fixture = CreateFixture(db, Mock.Of<IWorkflowEngine>());

        var startedBy = Guid.NewGuid();
        var pending = await fixture.Service.DispatchAsync(
            new WorkflowDispatchIntent(
                workflow.Id, "manual",
                new Dictionary<string, string> { ["password"] = "super-secret" },
                StartedByUserId: startedBy), CancellationToken.None);

        var persisted = await db.WorkflowExecutions.FindAsync(pending.Id);
        persisted!.Status.Should().Be(ExecutionStatus.Pending);
        persisted.StartedByUserId.Should().Be(startedBy);
        persisted.InputParametersJson.Should().Contain("\"password\"");
        persisted.InputParametersJson.Should().NotContain("super-secret");
        var outbox = await db.ExecutionDispatchOutbox.SingleAsync(x => x.ExecutionId == pending.Id);
        outbox.WorkflowId.Should().Be(workflow.Id);
        outbox.StartedByUserId.Should().Be(startedBy);
        outbox.ProtectedParameters.Should().NotBeNull();
    }

    [Fact]
    public async Task NotifyCommitted_RequestAlreadyCancelled_LeavesDurableIntentPending()
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = EnabledWorkflow();
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        await using var fixture = CreateFixture(db, Mock.Of<IWorkflowEngine>());
        var intent = new WorkflowDispatchIntent(workflow.Id, "manual", null);
        var pending = await fixture.Service.DispatchAsync(intent, CancellationToken.None);
        using var cancelledRequest = new CancellationTokenSource();
        await cancelledRequest.CancelAsync();

        var act = () => fixture.Service.NotifyCommitted();

        act.Should().NotThrow();
        (await db.ExecutionDispatchOutbox.AnyAsync(item => item.ExecutionId == pending.Id))
            .Should().BeTrue();
        (await db.WorkflowExecutions.AsNoTracking().SingleAsync()).Status
            .Should().Be(ExecutionStatus.Pending);
    }

    [Fact]
    public async Task ProcessOutbox_DisabledWorkflow_CancelsAndNotifiesSuppression()
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = EnabledWorkflow();
        workflow.IsEnabled = false;
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        var engine = new Mock<IWorkflowEngine>();
        await using var fixture = CreateFixture(db, engine.Object);
        WorkflowDispatchSuppression? suppression = null;
        var pending = await fixture.Service.DispatchAsync(
            new WorkflowDispatchIntent(
                workflow.Id, "scheduleTrigger", null,
                RequireWorkflowEnabled: true,
                OnDispatchSuppressedAsync: (value, _) =>
                {
                    suppression = value;
                    return Task.CompletedTask;
                }), CancellationToken.None);

        await fixture.Service.ProcessOutboxAsync(pending.Id, CancellationToken.None);

        db.ChangeTracker.Clear();
        var persisted = await db.WorkflowExecutions.SingleAsync();
        persisted.Status.Should().Be(ExecutionStatus.Cancelled);
        persisted.CancelledBy.Should().Be("dispatch");
        suppression!.Reason.Should().Be("workflow_disabled_before_dispatch");
        (await db.ExecutionDispatchOutbox.AnyAsync()).Should().BeFalse();
        VerifyEngineCalls(engine, Times.Never());
    }

    [Fact]
    public async Task ProcessOutbox_HoldsWorkerUntilEngineCompletes()
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = EnabledWorkflow();
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new Mock<IWorkflowEngine>();
        engine.Setup(candidate => candidate.ExecuteAsync(
                It.IsAny<Workflow>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<Dictionary<string, string>?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>(),
                It.IsAny<bool>()))
            .Returns(async () =>
            {
                started.TrySetResult();
                await release.Task;
                return new WorkflowExecution { Status = ExecutionStatus.Succeeded };
            });
        await using var fixture = CreateFixture(db, engine.Object);
        var pending = await fixture.Service.DispatchAsync(
            new WorkflowDispatchIntent(workflow.Id, "manual", null), CancellationToken.None);

        var workerTask = fixture.Service.ProcessOutboxAsync(pending.Id, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        workerTask.IsCompleted.Should().BeFalse();
        release.TrySetResult();
        (await workerTask.WaitAsync(TimeSpan.FromSeconds(2)))
            .Should().Be(ExecutionDispatchOutcome.Completed);
    }

    [Fact]
    public async Task ProcessOutbox_EngineClaimWasFenced_KeepsIntentAndSuppressionCallbackForRetry()
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = EnabledWorkflow();
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        var engine = new Mock<IWorkflowEngine>();
        engine.Setup(candidate => candidate.ExecuteAsync(
                It.IsAny<Workflow>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<Dictionary<string, string>?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>(),
                It.IsAny<bool>()))
            .ReturnsAsync(new WorkflowExecution { Status = ExecutionStatus.Pending });
        await using var fixture = CreateFixture(db, engine.Object);
        WorkflowDispatchSuppression? suppression = null;
        var pending = await fixture.Service.DispatchAsync(
            new WorkflowDispatchIntent(
                workflow.Id,
                "manual",
                null,
                RequireWorkflowEnabled: true,
                OnDispatchSuppressedAsync: (value, _) =>
                {
                    suppression = value;
                    return Task.CompletedTask;
                }), CancellationToken.None);

        var firstOutcome = await fixture.Service.ProcessOutboxAsync(
            pending.Id, CancellationToken.None);

        firstOutcome.Should().Be(ExecutionDispatchOutcome.RetryBeforeStart);
        (await db.ExecutionDispatchOutbox.AnyAsync(x => x.ExecutionId == pending.Id))
            .Should().BeTrue("a fenced engine claim never took ownership");

        await db.Workflows.Where(x => x.Id == workflow.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.IsEnabled, false));
        db.ChangeTracker.Clear();
        var secondOutcome = await fixture.Service.ProcessOutboxAsync(
            pending.Id, CancellationToken.None);

        secondOutcome.Should().Be(ExecutionDispatchOutcome.Completed);
        suppression!.Reason.Should().Be("workflow_disabled_before_dispatch");
        (await db.ExecutionDispatchOutbox.AnyAsync()).Should().BeFalse();
        VerifyEngineCalls(engine, Times.Once());
    }

    [Fact]
    public async Task ProcessOutbox_CapacityFailureAfterClaim_TerminalizesRunningGhost()
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = EnabledWorkflow();
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        var engine = new Mock<IWorkflowEngine>();
        engine.Setup(candidate => candidate.ExecuteAsync(
                It.IsAny<Workflow>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<Dictionary<string, string>?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>(),
                It.IsAny<bool>()))
            .Returns(async (Workflow _, string _, CancellationToken _, Dictionary<string, string>? _,
                int? _, bool _, Guid? _, Guid? _, int _, Guid? executionId, bool _) =>
            {
                await db.WorkflowExecutions
                    .Where(execution => execution.Id == executionId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(execution => execution.Status, ExecutionStatus.Running));
                throw new NodePilot.Core.Exceptions.ExecutionCapacityException("capacity reached");
            });
        await using var fixture = CreateFixture(db, engine.Object);
        var pending = await fixture.Service.DispatchAsync(
            new WorkflowDispatchIntent(workflow.Id, "manual", null), CancellationToken.None);

        var outcome = await fixture.Service.ProcessOutboxAsync(pending.Id, CancellationToken.None);

        outcome.Should().Be(ExecutionDispatchOutcome.Completed);
        var persisted = await db.WorkflowExecutions.AsNoTracking().SingleAsync();
        persisted.Status.Should().Be(ExecutionStatus.Failed);
        persisted.CompletedAt.Should().NotBeNull();
        persisted.ErrorMessage.Should().Contain("capacity reached");
        (await db.ExecutionDispatchOutbox.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ProcessOutbox_FailureAfterEngineInvocation_IsNotRetried()
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = EnabledWorkflow();
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        var engine = new Mock<IWorkflowEngine>();
        engine.Setup(candidate => candidate.ExecuteAsync(
                It.IsAny<Workflow>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<Dictionary<string, string>?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>(),
                It.IsAny<bool>()))
            .ThrowsAsync(new IOException("unknown outcome"));
        await using var fixture = CreateFixture(db, engine.Object);
        var pending = await fixture.Service.DispatchAsync(
            new WorkflowDispatchIntent(workflow.Id, "manual", null), CancellationToken.None);

        (await fixture.Service.ProcessOutboxAsync(pending.Id, CancellationToken.None))
            .Should().Be(ExecutionDispatchOutcome.Completed);
        (await fixture.Service.ProcessOutboxAsync(pending.Id, CancellationToken.None))
            .Should().Be(ExecutionDispatchOutcome.Completed);

        VerifyEngineCalls(engine, Times.Once());
        (await db.ExecutionDispatchOutbox.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ProcessOutbox_ScopeFailureBeforeEngineOwnership_PreservesDurableIntent()
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = EnabledWorkflow();
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        var service = new ExecutionDispatchService(
            db,
            new ThrowingScopeFactory(),
            new OutputRedactor(null),
            new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            NodePilot.TestCommons.StubMaintenanceWindowEvaluator.AllowAll,
            new NodePilot.Engine.Activities.InMemoryWorkflowConcurrencyGate(),
            NullLogger<ExecutionDispatchService>.Instance);
        var pending = await service.DispatchAsync(
            new WorkflowDispatchIntent(workflow.Id, "manual", null), CancellationToken.None);

        var outcome = await service.ProcessOutboxAsync(pending.Id, CancellationToken.None);

        outcome.Should().Be(ExecutionDispatchOutcome.RetryBeforeStart);
        (await db.ExecutionDispatchOutbox.AnyAsync(item => item.ExecutionId == pending.Id))
            .Should().BeTrue();
        (await db.WorkflowExecutions.AsNoTracking().SingleAsync()).Status
            .Should().Be(ExecutionStatus.Pending);
    }

    [Fact]
    public async Task ProcessOutbox_AtWorkflowConcurrencyLimit_LeavesExecutionPendingAndKeepsOutboxRow()
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = EnabledWorkflow();
        workflow.MaxConcurrentExecutions = 1;
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        var engine = new Mock<IWorkflowEngine>();

        var gate = new NodePilot.Engine.Activities.InMemoryWorkflowConcurrencyGate();
        gate.TryAcquire(workflow.Id, 1).Should().BeTrue(); // the one slot is already taken
        await using var fixture = CreateFixture(db, engine.Object, gate);
        var pending = await fixture.Service.DispatchAsync(
            new WorkflowDispatchIntent(workflow.Id, "manual", null), CancellationToken.None);

        var outcome = await fixture.Service.ProcessOutboxAsync(pending.Id, CancellationToken.None);

        // Queued, not failed: the run must survive to be dispatched once a slot frees.
        outcome.Should().Be(ExecutionDispatchOutcome.DeferredByConcurrencyLimit);
        VerifyEngineCalls(engine, Times.Never());
        (await db.ExecutionDispatchOutbox.AnyAsync(item => item.ExecutionId == pending.Id))
            .Should().BeTrue();
        (await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == pending.Id)).Status
            .Should().Be(ExecutionStatus.Pending);
    }

    [Fact]
    public async Task ProcessOutbox_WhenSlotFrees_RunsThePreviouslyDeferredExecution()
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = EnabledWorkflow();
        workflow.MaxConcurrentExecutions = 1;
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        var engine = SucceedingEngine();

        var gate = new NodePilot.Engine.Activities.InMemoryWorkflowConcurrencyGate();
        gate.TryAcquire(workflow.Id, 1);
        await using var fixture = CreateFixture(db, engine.Object, gate);
        var pending = await fixture.Service.DispatchAsync(
            new WorkflowDispatchIntent(workflow.Id, "manual", null), CancellationToken.None);
        await fixture.Service.ProcessOutboxAsync(pending.Id, CancellationToken.None);

        gate.Release(workflow.Id);
        var outcome = await fixture.Service.ProcessOutboxAsync(pending.Id, CancellationToken.None);

        outcome.Should().Be(ExecutionDispatchOutcome.Completed);
        VerifyEngineCalls(engine, Times.Once());
    }

    [Fact]
    public async Task ProcessOutbox_WithLimit_ReleasesTheSlotAfterTheRun()
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = EnabledWorkflow();
        workflow.MaxConcurrentExecutions = 1;
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        await using var fixture = CreateFixture(db, SucceedingEngine().Object);
        var pending = await fixture.Service.DispatchAsync(
            new WorkflowDispatchIntent(workflow.Id, "manual", null), CancellationToken.None);
        await fixture.Service.ProcessOutboxAsync(pending.Id, CancellationToken.None);

        fixture.Concurrency.BlockedWorkflowIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessOutbox_WhenEngineThrows_StillReleasesTheConcurrencySlot()
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = EnabledWorkflow();
        workflow.MaxConcurrentExecutions = 1;
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        var engine = new Mock<IWorkflowEngine>();
        engine.Setup(candidate => candidate.ExecuteAsync(
                It.IsAny<Workflow>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<Dictionary<string, string>?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>(),
                It.IsAny<bool>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        await using var fixture = CreateFixture(db, engine.Object);
        var pending = await fixture.Service.DispatchAsync(
            new WorkflowDispatchIntent(workflow.Id, "manual", null), CancellationToken.None);
        await fixture.Service.ProcessOutboxAsync(pending.Id, CancellationToken.None);

        // A leaked slot would wedge the workflow permanently.
        fixture.Concurrency.BlockedWorkflowIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessOutbox_BlockedByMaintenanceWindow_DoesNotConsumeAConcurrencySlot()
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = EnabledWorkflow();
        workflow.MaxConcurrentExecutions = 1;
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(Mock.Of<IWorkflowEngine>());
        await using var provider = services.BuildServiceProvider();
        var gate = new NodePilot.Engine.Activities.InMemoryWorkflowConcurrencyGate();
        var service = new ExecutionDispatchService(
            db,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new OutputRedactor(null),
            new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            NodePilot.TestCommons.StubMaintenanceWindowEvaluator.Blocking("PatchWindow"),
            gate,
            NullLogger<ExecutionDispatchService>.Instance);
        var pending = await service.DispatchAsync(
            new WorkflowDispatchIntent(workflow.Id, "schedule", null,
                RequireMaintenanceWindowCheck: true),
            CancellationToken.None);

        await service.ProcessOutboxAsync(pending.Id, CancellationToken.None);

        // The run ends Cancelled, so it must never have held a slot.
        gate.BlockedWorkflowIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessOutbox_WithNoLimit_BehavesAsBefore()
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = EnabledWorkflow();
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        var engine = SucceedingEngine();

        await using var fixture = CreateFixture(db, engine.Object);
        var pending = await fixture.Service.DispatchAsync(
            new WorkflowDispatchIntent(workflow.Id, "manual", null), CancellationToken.None);

        (await fixture.Service.ProcessOutboxAsync(pending.Id, CancellationToken.None))
            .Should().Be(ExecutionDispatchOutcome.Completed);
        VerifyEngineCalls(engine, Times.Once());
        fixture.Concurrency.BlockedWorkflowIds.Should().BeEmpty();
    }

    private static Mock<IWorkflowEngine> SucceedingEngine()
    {
        var engine = new Mock<IWorkflowEngine>();
        engine.Setup(candidate => candidate.ExecuteAsync(
                It.IsAny<Workflow>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<Dictionary<string, string>?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>(),
                It.IsAny<bool>()))
            .ReturnsAsync((Workflow wf, string _, CancellationToken _, Dictionary<string, string>? _,
                int? _, bool _, Guid? _, Guid? _, int _, Guid? executionId, bool _) =>
                new WorkflowExecution
                {
                    Id = executionId ?? Guid.NewGuid(),
                    WorkflowId = wf.Id,
                    Status = ExecutionStatus.Succeeded,
                });
        return engine;
    }

    private static void VerifyEngineCalls(Mock<IWorkflowEngine> engine, Times times)
        => engine.Verify(candidate => candidate.ExecuteAsync(
            It.IsAny<Workflow>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
            It.IsAny<Dictionary<string, string>?>(), It.IsAny<int?>(), It.IsAny<bool>(),
            It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>(),
            It.IsAny<bool>()), times);

    private static Workflow EnabledWorkflow() => new()
    {
        Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}", IsEnabled = true,
    };

    private static Fixture CreateFixture(
        NodePilotDbContext db,
        IWorkflowEngine engine,
        IWorkflowConcurrencyGate? concurrency = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(engine);
        var provider = services.BuildServiceProvider();
        var gate = concurrency ?? new NodePilot.Engine.Activities.InMemoryWorkflowConcurrencyGate();
        var service = new ExecutionDispatchService(
            db,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new OutputRedactor(null),
            new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            NodePilot.TestCommons.StubMaintenanceWindowEvaluator.AllowAll,
            gate,
            NullLogger<ExecutionDispatchService>.Instance);
        return new Fixture(provider, service, gate);
    }

    private sealed class Fixture(
        ServiceProvider provider, ExecutionDispatchService service, IWorkflowConcurrencyGate concurrency)
        : IAsyncDisposable
    {
        public ExecutionDispatchService Service { get; } = service;
        public IWorkflowConcurrencyGate Concurrency { get; } = concurrency;
        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("scope unavailable");
    }
}
