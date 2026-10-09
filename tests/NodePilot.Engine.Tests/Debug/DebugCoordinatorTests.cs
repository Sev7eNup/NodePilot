using FluentAssertions;
using Moq;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Debug;
using NodePilot.Engine.Security;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests.Debug;

public class DebugCoordinatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pause_IsResumableAsSoonAsItBecomesVisible(bool resumeFromPersistence)
    {
        await using var db = TestDbContext.Create();
        using var cancellation = new CancellationTokenSource();
        var (execution, step, node) = await SeedAsync(db);
        var handle = new DebugHandle();
        var notifier = new Mock<IExecutionNotifier>();
        bool? accepted = null;
        void Resume()
        {
            accepted = handle.Resume(node.Id, new ResumeRequest(ResumeCommand.Continue, null));
            // Baseline must finish deterministically even when the early command is lost.
            if (accepted == false) cancellation.Cancel();
        }
        if (resumeFromPersistence)
            db.SavedChanges += (_, _) => { if (step.Status == ExecutionStatus.Paused) Resume(); };
        else
            notifier.Setup(n => n.StepPausedAsync(execution.Id, execution.WorkflowId, node.Id,
                    It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                    It.IsAny<DateTime>(), It.IsAny<string>()))
                .Callback(Resume).Returns(Task.CompletedTask);
        var coordinator = new DebugCoordinator(new OutputRedactor(null), notifier.Object);
        try
        {
            await coordinator.HandlePauseAsync(execution, node, step, db, new(), handle,
                cancellation, cancellation.Token);
        }
        catch (OperationCanceledException) when (accepted == false) { }

        accepted.Should().BeTrue("a persisted or announced pause must already accept its resume command");
        step.Status.Should().Be(ExecutionStatus.Running);
        handle.PendingSteps.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pause_NotificationFailureOrCancellation_RemovesPendingContinuation(bool cancel)
    {
        await using var db = TestDbContext.Create();
        using var cancellation = new CancellationTokenSource();
        var (execution, step, node) = await SeedAsync(db);
        var handle = new DebugHandle();
        var notifier = new Mock<IExecutionNotifier>();
        notifier.Setup(n => n.StepPausedAsync(execution.Id, execution.WorkflowId, node.Id,
                It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<DateTime>(), It.IsAny<string>()))
            .Returns(() =>
            {
                if (!cancel) throw new InvalidOperationException("notification unavailable");
                cancellation.Cancel();
                return Task.CompletedTask;
            });
        var coordinator = new DebugCoordinator(new OutputRedactor(null), notifier.Object);
        var work = () => coordinator.HandlePauseAsync(execution, node, step, db, new(), handle,
            cancellation, cancellation.Token);
        if (cancel) await work.Should().ThrowAsync<OperationCanceledException>();
        else await work.Should().ThrowAsync<InvalidOperationException>();
        handle.PendingSteps.Should().BeEmpty();
    }

    private static async Task<(WorkflowExecution, StepExecution, WorkflowNode)> SeedAsync(NodePilot.Data.NodePilotDbContext db)
    {
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "Debug lifecycle", DefinitionJson = "{}" };
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, Status = ExecutionStatus.Running,
            StartedAt = DateTime.UtcNow, TriggeredBy = "debug",
        };
        var step = new StepExecution
        {
            Id = Guid.NewGuid(), WorkflowExecutionId = execution.Id, StepId = "script",
            StepType = "runScript", Status = ExecutionStatus.Running, StartedAt = DateTime.UtcNow,
        };
        db.AddRange(workflow, execution, step);
        await db.SaveChangesAsync();
        return (execution, step, new WorkflowNode { Id = step.StepId, Type = step.StepType, Data = new() });
    }
}
