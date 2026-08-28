using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using Moq;
using NodePilot.Api.ExecutionDispatch;
using NodePilot.Core.ExecutionDispatch;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Availability;
using NodePilot.Engine.Security;
using Xunit;

namespace NodePilot.Api.Tests.ExecutionDispatch;

public class ExecutionDispatchServiceTests
{
    private sealed class FailOnceOnPendingReadInterceptor : DbCommandInterceptor
    {
        private readonly DatabaseAvailabilityTracker _availability;
        private int _fired;
        internal TaskCompletionSource Fired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal FailOnceOnPendingReadInterceptor(DatabaseAvailabilityTracker availability)
            => _availability = availability;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            MaybeThrow(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            MaybeThrow(command);
            return ValueTask.FromResult(result);
        }

        private void MaybeThrow(DbCommand command)
        {
            if (!command.CommandText.Contains("WorkflowExecutions", StringComparison.Ordinal)
                || Interlocked.Exchange(ref _fired, 1) != 0)
            {
                return;
            }

            _availability.ReportUnreachable(DatabaseOutageReason.Unreachable);
            Fired.TrySetResult();
            throw new IOException("simulated database outage before engine start");
        }
    }

    [Fact]
    public async Task DispatchAsync_CreatesRedactedOwnerStampedPendingAndEnqueues()
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}", IsEnabled = true };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var queue = new CapturingExecutionDispatchQueue();
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(Mock.Of<IWorkflowEngine>());
        var provider = services.BuildServiceProvider();
        var cluster = new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider();
        var service = new ExecutionDispatchService(
            db,
            queue,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new OutputRedactor(null),
            cluster,
            NodePilot.TestCommons.StubMaintenanceWindowEvaluator.AllowAll,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExecutionDispatchService>.Instance);

        var startedBy = Guid.NewGuid();
        var pending = await service.DispatchAsync(
            new WorkflowDispatchIntent(
                workflow.Id,
                "manual",
                new Dictionary<string, string> { ["password"] = "super-secret" },
                StartedByUserId: startedBy),
            CancellationToken.None);

        var persisted = await db.WorkflowExecutions.FindAsync(pending.Id);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(ExecutionStatus.Pending);
        persisted.StartedByUserId.Should().Be(startedBy);
        persisted.OwnerNodeId.Should().Be(cluster.NodeId);
        persisted.InputParametersJson.Should().Contain("\"password\"");
        persisted.InputParametersJson.Should().NotContain("super-secret");
        queue.EnqueueCount.Should().Be(1);
    }

    [Fact]
    public async Task EnqueueAsync_RequestCancelledAfterPendingPersist_DoesNotCancelDispatch()
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        var pending = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Pending,
            StartedAt = DateTime.UtcNow,
            TriggeredBy = "manual",
        };
        db.Workflows.Add(workflow);
        db.WorkflowExecutions.Add(pending);
        await db.SaveChangesAsync();

        var queue = new CapturingExecutionDispatchQueue();
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(Mock.Of<IWorkflowEngine>());
        var provider = services.BuildServiceProvider();
        var service = new ExecutionDispatchService(
            db,
            queue,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new OutputRedactor(null),
            new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            NodePilot.TestCommons.StubMaintenanceWindowEvaluator.AllowAll,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExecutionDispatchService>.Instance);

        using var cancelledRequest = new CancellationTokenSource();
        await cancelledRequest.CancelAsync();

        await service.EnqueueAsync(
            pending,
            new WorkflowDispatchIntent(workflow.Id, "manual", null),
            cancelledRequest.Token);

        queue.EnqueueCount.Should().Be(1);
        queue.EnqueueToken.CanBeCanceled.Should().BeFalse();
        queue.Priority.Should().Be(ExecutionDispatchPriority.Normal);

        var persisted = await db.WorkflowExecutions.FindAsync(pending.Id);
        persisted!.Status.Should().Be(ExecutionStatus.Pending);
        persisted.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task EnqueueAsync_InteractivePriority_UsesPriorityQueueAndDoesNotBypassWorkerPool()
    {
        // Interactive runs (Test-button/webhook) must be preferred by the dispatch queue,
        // but still consume a bounded worker slot instead of creating an unbounded Task.
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}", IsEnabled = true };
        var pending = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Pending,
            StartedAt = DateTime.UtcNow,
            TriggeredBy = "manual",
        };
        db.Workflows.Add(workflow);
        db.WorkflowExecutions.Add(pending);
        await db.SaveChangesAsync();

        var engineCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engineMock = new Mock<IWorkflowEngine>();
        engineMock.Setup(e => e.ExecuteAsync(
                It.IsAny<Workflow>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<Dictionary<string, string>?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>(),
                It.IsAny<bool>()))
            .Returns(async () =>
            {
                engineCalled.TrySetResult();
                return pending;
            });

        var queue = new CapturingExecutionDispatchQueue();
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(engineMock.Object);
        var provider = services.BuildServiceProvider();
        var service = new ExecutionDispatchService(
            db,
            queue,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new OutputRedactor(null),
            new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            NodePilot.TestCommons.StubMaintenanceWindowEvaluator.AllowAll,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExecutionDispatchService>.Instance);

        await service.EnqueueAsync(
            pending,
            new WorkflowDispatchIntent(workflow.Id, "manual", null,
                Priority: ExecutionDispatchPriority.Interactive),
            CancellationToken.None);

        queue.EnqueueCount.Should().Be(1, "Interactive runs must still use the bounded worker pool");
        queue.Priority.Should().Be(ExecutionDispatchPriority.Interactive);
        queue.CapturedWorkItem.Should().NotBeNull();
        engineCalled.Task.IsCompleted.Should().BeFalse(
            "enqueue should not execute the workflow until a dispatch worker takes the item");

        await queue.CapturedWorkItem!(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await engineCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EnqueueAsync_DisabledWorkflowAtDispatch_CancelsPendingAndInvokesSuppressionCallback()
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}", IsEnabled = false };
        var pending = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Pending,
            StartedAt = DateTime.UtcNow,
            TriggeredBy = "scheduleTrigger",
        };
        db.Workflows.Add(workflow);
        db.WorkflowExecutions.Add(pending);
        await db.SaveChangesAsync();

        var engineMock = new Mock<IWorkflowEngine>();
        var queue = new CapturingExecutionDispatchQueue();
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(engineMock.Object);
        var provider = services.BuildServiceProvider();
        var service = new ExecutionDispatchService(
            db,
            queue,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new OutputRedactor(null),
            new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            NodePilot.TestCommons.StubMaintenanceWindowEvaluator.AllowAll,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExecutionDispatchService>.Instance);

        WorkflowDispatchSuppression? suppression = null;
        await service.EnqueueAsync(
            pending,
            new WorkflowDispatchIntent(
                workflow.Id,
                "scheduleTrigger",
                null,
                RequireWorkflowEnabled: true,
                OnDispatchSuppressedAsync: (s, _) =>
                {
                    suppression = s;
                    return Task.CompletedTask;
                }),
            CancellationToken.None);

        await queue.CapturedWorkItem!(CancellationToken.None);

        var persisted = await db.WorkflowExecutions.FindAsync(pending.Id);
        persisted!.Status.Should().Be(ExecutionStatus.Cancelled);
        persisted.CompletedAt.Should().NotBeNull();
        suppression.Should().NotBeNull();
        suppression!.Reason.Should().Be("workflow_disabled_before_dispatch");
        engineMock.Verify(e => e.ExecuteAsync(
                It.IsAny<Workflow>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<Dictionary<string, string>?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>(),
                It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecutionDispatchQueue_DequeueAsync_PrefersInteractiveWorkItems()
    {
        var queue = new ExecutionDispatchQueue(
            Options.Create(new ExecutionDispatchOptions { Capacity = 10, WorkerCount = 1 }));

        Func<CancellationToken, Task> normal = _ => Task.CompletedTask;
        Func<CancellationToken, Task> interactive = _ => Task.CompletedTask;

        await queue.EnqueueAsync(normal, CancellationToken.None);
        await queue.EnqueueAsync(interactive, CancellationToken.None, ExecutionDispatchPriority.Interactive);

        var first = await queue.DequeueAsync(CancellationToken.None);

        first.Should().BeSameAs(interactive);
    }

    [Fact]
    public async Task EnqueueAsync_WorkerCallback_AwaitsEngineForFullWorkflowLifetime()
    {
        // Backpressure contract: the worker callback must AWAIT engine.ExecuteAsync for the
        // entire workflow lifetime. This makes WorkerCount the real concurrency cap and the
        // queue (Capacity) the spike buffer — starts beyond WorkerCount wait in the queue
        // instead of all hitting the engine at once and tripping the per-user/global caps.
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}", IsEnabled = true };
        var pending = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Pending,
            StartedAt = DateTime.UtcNow,
            TriggeredBy = "manual",
        };
        db.Workflows.Add(workflow);
        db.WorkflowExecutions.Add(pending);
        await db.SaveChangesAsync();

        // Engine that simulates a long-running workflow. The worker callback must not
        // return until the engine has finished.
        var engineStarted = new TaskCompletionSource();
        var releaseEngine = new TaskCompletionSource();
        var engineMock = new Mock<IWorkflowEngine>();
        engineMock.Setup(e => e.ExecuteAsync(
                It.IsAny<Workflow>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<Dictionary<string, string>?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>(),
                It.IsAny<bool>()))
            .Returns(async () =>
            {
                engineStarted.TrySetResult();
                await releaseEngine.Task;
                return pending;
            });

        var queue = new CapturingExecutionDispatchQueue();
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(engineMock.Object);
        var provider = services.BuildServiceProvider();
        var service = new ExecutionDispatchService(
            db,
            queue,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new OutputRedactor(null),
            new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            NodePilot.TestCommons.StubMaintenanceWindowEvaluator.AllowAll,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExecutionDispatchService>.Instance);

        await service.EnqueueAsync(
            pending,
            new WorkflowDispatchIntent(workflow.Id, "manual", null, RequireWorkflowEnabled: true),
            CancellationToken.None);

        queue.CapturedWorkItem.Should().NotBeNull();

        // Worker invokes the captured callback. The callback must NOT complete while the
        // engine is still running — that would mean it released the worker slot prematurely.
        var workerTask = queue.CapturedWorkItem!(CancellationToken.None);
        await engineStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        workerTask.IsCompleted.Should().BeFalse(
            "the worker callback must hold its slot until the engine finishes");

        // Once the engine completes, the worker callback must complete promptly.
        releaseEngine.SetResult();
        await workerTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Worker_DatabaseFailureBeforeEngineStart_RequeuesAndStartsExactlyOnceAfterRecovery()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using var db = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}", IsEnabled = true };
        var pending = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Pending,
            StartedAt = DateTime.UtcNow,
            TriggeredBy = "manual",
        };
        db.AddRange(workflow, pending);
        await db.SaveChangesAsync();

        var availability = new DatabaseAvailabilityTracker(
            NullLogger<DatabaseAvailabilityTracker>.Instance,
            probeSuccessesToRecover: 1);
        availability.MarkBootComplete();
        var failPendingRead = new FailOnceOnPendingReadInterceptor(availability);
        var engineCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new Mock<IWorkflowEngine>();
        engine.Setup(candidate => candidate.ExecuteAsync(
                It.IsAny<Workflow>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<Dictionary<string, string>?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>(),
                It.IsAny<bool>()))
            .Returns(() =>
            {
                engineCalled.TrySetResult();
                return Task.FromResult(pending);
            });

        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(options =>
            options.UseSqlite(connection).AddInterceptors(failPendingRead));
        services.AddSingleton(engine.Object);
        await using var provider = services.BuildServiceProvider();
        var queue = new ExecutionDispatchQueue(
            Options.Create(new ExecutionDispatchOptions { Capacity = 10, WorkerCount = 1 }));
        var service = new ExecutionDispatchService(
            db,
            queue,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new OutputRedactor(null),
            new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            NodePilot.TestCommons.StubMaintenanceWindowEvaluator.AllowAll,
            NullLogger<ExecutionDispatchService>.Instance,
            availability);
        var worker = new ExecutionDispatchWorker(
            queue,
            Options.Create(new ExecutionDispatchOptions { Capacity = 10, WorkerCount = 1 }),
            new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            NullLogger<ExecutionDispatchWorker>.Instance,
            availability);

        await service.EnqueueAsync(
            pending,
            new WorkflowDispatchIntent(workflow.Id, "manual", null),
            CancellationToken.None);
        using var stopCts = new CancellationTokenSource();
        await worker.StartAsync(stopCts.Token);

        await failPendingRead.Fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(150);
        engineCalled.Task.IsCompleted.Should().BeFalse(
            "the before-start item must park rather than terminalize or disappear");

        availability.ReportProbeSucceeded();
        await engineCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        engine.Verify(candidate => candidate.ExecuteAsync(
                It.IsAny<Workflow>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<Dictionary<string, string>?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>(),
                It.IsAny<bool>()),
            Times.Once);

        await stopCts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Worker_DatabaseFailureAfterEngineInvocation_DoesNotRequeue()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using var db = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}", IsEnabled = true };
        var pending = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Pending,
            StartedAt = DateTime.UtcNow,
            TriggeredBy = "manual",
        };
        db.AddRange(workflow, pending);
        await db.SaveChangesAsync();

        var availability = new DatabaseAvailabilityTracker(
            NullLogger<DatabaseAvailabilityTracker>.Instance,
            probeSuccessesToRecover: 1);
        availability.MarkBootComplete();
        var engineStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new Mock<IWorkflowEngine>();
        engine.Setup(candidate => candidate.ExecuteAsync(
                It.IsAny<Workflow>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<Dictionary<string, string>?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>(),
                It.IsAny<bool>()))
            .Returns(() =>
            {
                availability.ReportUnreachable(DatabaseOutageReason.Unreachable);
                engineStarted.TrySetResult();
                throw new IOException("unknown outcome after engine invocation");
            });

        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton(engine.Object);
        await using var provider = services.BuildServiceProvider();
        var queue = new ExecutionDispatchQueue(
            Options.Create(new ExecutionDispatchOptions { Capacity = 10, WorkerCount = 1 }));
        var service = new ExecutionDispatchService(
            db,
            queue,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new OutputRedactor(null),
            new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            NodePilot.TestCommons.StubMaintenanceWindowEvaluator.AllowAll,
            NullLogger<ExecutionDispatchService>.Instance,
            availability);
        var worker = new ExecutionDispatchWorker(
            queue,
            Options.Create(new ExecutionDispatchOptions { Capacity = 10, WorkerCount = 1 }),
            new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            NullLogger<ExecutionDispatchWorker>.Instance,
            availability);

        await service.EnqueueAsync(
            pending,
            new WorkflowDispatchIntent(workflow.Id, "manual", null),
            CancellationToken.None);
        using var stopCts = new CancellationTokenSource();
        await worker.StartAsync(stopCts.Token);
        await engineStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        availability.ReportProbeSucceeded();
        await Task.Delay(250);
        engine.Verify(candidate => candidate.ExecuteAsync(
                It.IsAny<Workflow>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<Dictionary<string, string>?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>(),
                It.IsAny<bool>()),
            Times.Once,
            "once engine invocation begins its claim/side effects are unknown and retry is unsafe");

        await stopCts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);
    }

    private sealed class CapturingExecutionDispatchQueue : IExecutionDispatchQueue
    {
        public int EnqueueCount { get; private set; }
        public CancellationToken EnqueueToken { get; private set; }
        public ExecutionDispatchPriority Priority { get; private set; }
        public Func<CancellationToken, Task>? CapturedWorkItem { get; private set; }

        public ValueTask EnqueueAsync(
            Func<CancellationToken, Task> workItem,
            CancellationToken ct,
            ExecutionDispatchPriority priority = ExecutionDispatchPriority.Normal)
        {
            ArgumentNullException.ThrowIfNull(workItem);
            EnqueueCount++;
            EnqueueToken = ct;
            Priority = priority;
            CapturedWorkItem = workItem;
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
