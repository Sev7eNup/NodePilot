using FluentAssertions;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Data.Tests;

/// <summary>
/// <see cref="ExecutionLogReader"/> — the data source behind the AI chat's execution-log
/// tools. SQLite in-memory; redaction runs through the deterministic <see cref="StubAuditDetailsRedactor"/>.
/// </summary>
public class ExecutionLogReaderTests
{
    private static readonly DateTime T0 = new(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc);

    private static Workflow SeedWorkflow(NodePilotDbContext db, string name = "wf")
    {
        var wf = new Workflow { Id = Guid.NewGuid(), Name = name, DefinitionJson = "{}" };
        db.Workflows.Add(wf);
        return wf;
    }

    private static WorkflowExecution SeedExecution(NodePilotDbContext db, Guid workflowId,
        ExecutionStatus status = ExecutionStatus.Succeeded, DateTime? startedAt = null, string? errorMessage = null)
    {
        var exec = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflowId,
            Status = status,
            StartedAt = startedAt ?? T0,
            CompletedAt = (startedAt ?? T0).AddSeconds(10),
            TriggeredBy = "manual",
            ErrorMessage = errorMessage,
        };
        db.WorkflowExecutions.Add(exec);
        return exec;
    }

    private static StepExecution SeedStep(NodePilotDbContext db, Guid executionId, string stepId,
        ExecutionStatus status = ExecutionStatus.Succeeded, DateTime? startedAt = null,
        string? stepName = null, string? output = null, string? errorOutput = null)
    {
        var step = new StepExecution
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = executionId,
            StepId = stepId,
            StepName = stepName,
            StepType = "runScript",
            Status = status,
            StartedAt = startedAt ?? T0,
            CompletedAt = (startedAt ?? T0).AddSeconds(1),
            Output = output,
            ErrorOutput = errorOutput,
        };
        db.StepExecutions.Add(step);
        return step;
    }

    [Fact]
    public async Task GetRecentExecutionsAsync_ReturnsNewestFirst_WithStepCountsAndFailedStepNames()
    {
        await using var db = TestDbFactory.Create();
        var wf = SeedWorkflow(db);
        var older = SeedExecution(db, wf.Id, ExecutionStatus.Failed, T0, errorMessage: "kaputt");
        var newer = SeedExecution(db, wf.Id, ExecutionStatus.Succeeded, T0.AddMinutes(5));
        SeedStep(db, older.Id, "s1", ExecutionStatus.Succeeded, T0);
        SeedStep(db, older.Id, "s2", ExecutionStatus.Failed, T0.AddSeconds(2), stepName: "Copy Files");
        SeedStep(db, older.Id, "s3", ExecutionStatus.Failed, T0.AddSeconds(4)); // unlabeled
        SeedStep(db, newer.Id, "s1", ExecutionStatus.Succeeded, T0.AddMinutes(5));
        await db.SaveChangesAsync();

        var reader = new ExecutionLogReader(db, new StubAuditDetailsRedactor());
        var result = await reader.GetRecentExecutionsAsync(wf.Id, 10, CancellationToken.None);

        result.Should().HaveCount(2);
        result[0].Id.Should().Be(newer.Id); // newest first
        result[0].StepsTotal.Should().Be(1);
        result[0].FailedSteps.Should().BeEmpty();
        result[1].Id.Should().Be(older.Id);
        result[1].Status.Should().Be("Failed");
        result[1].ErrorMessage.Should().Be("kaputt");
        result[1].StepsTotal.Should().Be(3);
        result[1].FailedSteps.Should().HaveCount(2);
        result[1].FailedSteps[0].Should().Be(new FailedStepInfo("s2", "Copy Files"));
        result[1].FailedSteps[1].Should().Be(new FailedStepInfo("s3", null)); // StepId stays stable even without a name
    }

    [Fact]
    public async Task GetRecentExecutionsAsync_OtherWorkflowsRuns_Excluded()
    {
        await using var db = TestDbFactory.Create();
        var mine = SeedWorkflow(db, "mine");
        var other = SeedWorkflow(db, "other");
        SeedExecution(db, mine.Id);
        SeedExecution(db, other.Id);
        await db.SaveChangesAsync();

        var reader = new ExecutionLogReader(db, new StubAuditDetailsRedactor());
        var result = await reader.GetRecentExecutionsAsync(mine.Id, 10, CancellationToken.None);

        result.Should().ContainSingle();
    }

    [Fact]
    public async Task GetRecentExecutionsAsync_TakeOutOfRange_Clamped()
    {
        await using var db = TestDbFactory.Create();
        var wf = SeedWorkflow(db);
        for (var i = 0; i < 25; i++)
            SeedExecution(db, wf.Id, startedAt: T0.AddMinutes(i));
        await db.SaveChangesAsync();

        var reader = new ExecutionLogReader(db, new StubAuditDetailsRedactor());
        (await reader.GetRecentExecutionsAsync(wf.Id, 999, CancellationToken.None)).Should().HaveCount(20);
        (await reader.GetRecentExecutionsAsync(wf.Id, 0, CancellationToken.None)).Should().HaveCount(1);
        (await reader.GetRecentExecutionsAsync(wf.Id, -5, CancellationToken.None)).Should().HaveCount(1);
    }

    [Fact]
    public async Task GetRecentExecutionsAsync_RedactsErrorMessage()
    {
        // Sentinel for the list path: without this test, the Redact(e.ErrorMessage) call in
        // GetRecentExecutionsAsync could be deleted unnoticed (the steps path has its own separate sentinel test).
        await using var db = TestDbFactory.Create();
        var wf = SeedWorkflow(db);
        SeedExecution(db, wf.Id, ExecutionStatus.Failed, errorMessage: "password hunter2 leaked");
        await db.SaveChangesAsync();

        var reader = new ExecutionLogReader(db, new StubAuditDetailsRedactor());
        var result = await reader.GetRecentExecutionsAsync(wf.Id, 10, CancellationToken.None);

        result.Single().ErrorMessage.Should().Be("password *** leaked");
    }

    [Fact]
    public async Task GetExecutionStepsAsync_ExecutionOfOtherWorkflow_ReturnsNull()
    {
        await using var db = TestDbFactory.Create();
        var mine = SeedWorkflow(db, "mine");
        var other = SeedWorkflow(db, "other");
        var foreignExec = SeedExecution(db, other.Id);
        await db.SaveChangesAsync();

        var reader = new ExecutionLogReader(db, new StubAuditDetailsRedactor());
        var result = await reader.GetExecutionStepsAsync(mine.Id, foreignExec.Id, CancellationToken.None);

        result.Should().BeNull(); // Ownership check: an execution from another workflow is treated as "does not exist"
    }

    [Fact]
    public async Task GetExecutionStepsAsync_RedactsOutputAndErrorOutputAndErrorMessage()
    {
        await using var db = TestDbFactory.Create();
        var wf = SeedWorkflow(db);
        var exec = SeedExecution(db, wf.Id, ExecutionStatus.Failed, errorMessage: "password hunter2 leaked");
        SeedStep(db, exec.Id, "s1", ExecutionStatus.Failed,
            output: "token=hunter2", errorOutput: "auth with hunter2 failed");
        await db.SaveChangesAsync();

        var reader = new ExecutionLogReader(db, new StubAuditDetailsRedactor());
        var result = await reader.GetExecutionStepsAsync(wf.Id, exec.Id, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Execution.ErrorMessage.Should().Be("password *** leaked");
        result.Steps.Single().Output.Should().Be("token=***");
        result.Steps.Single().ErrorOutput.Should().Be("auth with *** failed");
    }

    [Fact]
    public async Task GetExecutionStepsAsync_OrdersStepsByStartedAt_AndBuildsSummary()
    {
        await using var db = TestDbFactory.Create();
        var wf = SeedWorkflow(db);
        var exec = SeedExecution(db, wf.Id, ExecutionStatus.Failed);
        SeedStep(db, exec.Id, "later", ExecutionStatus.Failed, T0.AddSeconds(30));
        SeedStep(db, exec.Id, "first", ExecutionStatus.Succeeded, T0.AddSeconds(1));
        await db.SaveChangesAsync();

        var reader = new ExecutionLogReader(db, new StubAuditDetailsRedactor());
        var result = await reader.GetExecutionStepsAsync(wf.Id, exec.Id, CancellationToken.None);

        result!.Steps.Select(s => s.StepId).Should().ContainInOrder("first", "later");
        result.Execution.StepsTotal.Should().Be(2);
        result.Execution.FailedSteps.Should().ContainSingle().Which.StepId.Should().Be("later");
    }
}
