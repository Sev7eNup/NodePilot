using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Data.Tests;

public sealed class ExecutionStateLifecycleTests : IDisposable
{
    private readonly NodePilotDbContext _db = TestDbFactory.Create();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CancelOrphanedStepsAsync_SweepsOnlyNonTerminalStepsOfTheGivenExecutions()
    {
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "wf", DefinitionJson = "{}" };
        var finished = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = workflow.Id, Status = ExecutionStatus.Failed };
        var other = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = workflow.Id, Status = ExecutionStatus.Running };
        var running = Step(finished.Id, "running", ExecutionStatus.Running);
        var paused = Step(finished.Id, "paused", ExecutionStatus.Paused);
        var succeeded = Step(finished.Id, "done", ExecutionStatus.Succeeded);
        var failedWithOutput = Step(finished.Id, "failed", ExecutionStatus.Failed);
        failedWithOutput.ErrorOutput = "real error";
        var otherRunning = Step(other.Id, "elsewhere", ExecutionStatus.Running);
        _db.AddRange(workflow, finished, other, running, paused, succeeded, failedWithOutput, otherRunning);
        await _db.SaveChangesAsync();
        var completedAt = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

        var swept = await ExecutionStateLifecycle.CancelOrphanedStepsAsync(
            _db.StepExecutions, [finished.Id], completedAt, "orphaned", CancellationToken.None);

        swept.Should().Be(2);
        _db.ChangeTracker.Clear();
        var steps = await _db.StepExecutions.ToDictionaryAsync(s => s.StepId);
        steps["running"].Should().Match<StepExecution>(s =>
            s.Status == ExecutionStatus.Cancelled && s.CompletedAt == completedAt && s.ErrorOutput == "orphaned");
        steps["paused"].Status.Should().Be(ExecutionStatus.Cancelled);
        steps["done"].Status.Should().Be(ExecutionStatus.Succeeded);
        steps["failed"].ErrorOutput.Should().Be("real error", "terminal rows are never touched");
        steps["elsewhere"].Status.Should().Be(ExecutionStatus.Running, "other executions are not in the sweep");
    }

    [Fact]
    public async Task CancelOrphanedStepsAsync_KeepsAnExistingErrorOutput()
    {
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "wf", DefinitionJson = "{}" };
        var execution = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = workflow.Id, Status = ExecutionStatus.Cancelled };
        var step = Step(execution.Id, "s1", ExecutionStatus.Running);
        step.ErrorOutput = "partial stderr";
        _db.AddRange(workflow, execution, step);
        await _db.SaveChangesAsync();

        await ExecutionStateLifecycle.CancelOrphanedStepsAsync(
            _db.StepExecutions, [execution.Id], DateTime.UtcNow, "orphaned", CancellationToken.None);

        _db.ChangeTracker.Clear();
        (await _db.StepExecutions.SingleAsync()).ErrorOutput.Should().Be("partial stderr");
    }

    private static StepExecution Step(Guid executionId, string stepId, ExecutionStatus status) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowExecutionId = executionId,
        StepId = stepId,
        StepType = "runScript",
        Status = status,
        StartedAt = DateTime.UtcNow,
    };
}
