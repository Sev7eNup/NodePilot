using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Engine.Execution;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Engine.Tests;

/// <summary>
/// Single-node (reconciler) semantics for <see cref="StartupRecovery"/>. The single-node path
/// recovers every non-terminal row and — critically — does it in bounded batches so a crash with
/// a large in-flight backlog never materializes the whole table into memory. The large-backlog
/// test proves the batch loop drains past a single <c>Take(batchSize)</c>.
/// </summary>
public sealed class StartupRecoverySingleNodeTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly Data.NodePilotDbContext _db;

    public StartupRecoverySingleNodeTests()
    {
        var (conn, db) = TestDbFactory.CreateWithConnection();
        _connection = conn;
        _db = db;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private Workflow SeedWorkflow()
    {
        var wf = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "wf",
            DefinitionJson = "{\"nodes\":[],\"edges\":[]}",
            IsEnabled = true,
            Version = 1
        };
        _db.Workflows.Add(wf);
        _db.SaveChanges();
        return wf;
    }

    [Fact]
    public async Task SingleNode_RecoversBacklogLargerThanOneBatch_AllCancelled()
    {
        // 1150 > the 500-row batch size, so recovery must loop at least three times. Before the
        // bounding fix this path did a single unbounded .ToListAsync() of every orphan.
        var wf = SeedWorkflow();
        const int total = 1150;
        var ids = new List<Guid>(total);
        var executions = new List<WorkflowExecution>(total);
        for (var i = 0; i < total; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            executions.Add(new WorkflowExecution
            {
                Id = id,
                WorkflowId = wf.Id,
                Status = ExecutionStatus.Running,
                StartedAt = DateTime.UtcNow.AddMinutes(-5)
            });
        }
        _db.WorkflowExecutions.AddRange(executions);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();

        var recovered = await StartupRecovery.RecoverOrphanedExecutionsAsync(_db, NullLogger.Instance);

        recovered.Should().Be(total);
        var remainingNonTerminal = _db.WorkflowExecutions
            .Count(e => e.Status == ExecutionStatus.Running
                     || e.Status == ExecutionStatus.Pending
                     || e.Status == ExecutionStatus.Paused);
        remainingNonTerminal.Should().Be(0, "the bounded loop must drain the entire backlog, not just one batch");
        _db.WorkflowExecutions.Count(e => e.Status == ExecutionStatus.Cancelled).Should().Be(total);
    }

    [Fact]
    public async Task SingleNode_PreservesPerStatusMessages_AndReconcilerMarker()
    {
        var wf = SeedWorkflow();
        var running = AddExecution(wf.Id, ExecutionStatus.Running);
        var pending = AddExecution(wf.Id, ExecutionStatus.Pending);
        var paused = AddExecution(wf.Id, ExecutionStatus.Paused);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();

        var recovered = await StartupRecovery.RecoverOrphanedExecutionsAsync(_db, NullLogger.Instance);

        recovered.Should().Be(3);
        var runningRow = (await _db.WorkflowExecutions.FindAsync(running))!;
        var pendingRow = (await _db.WorkflowExecutions.FindAsync(pending))!;
        var pausedRow = (await _db.WorkflowExecutions.FindAsync(paused))!;

        runningRow.Status.Should().Be(ExecutionStatus.Cancelled);
        runningRow.CancelledBy.Should().Be("reconciler");
        runningRow.ErrorMessage.Should().Contain("orphaned by an API process restart");
        pendingRow.ErrorMessage.Should().Contain("queued but not dispatched");
        pausedRow.ErrorMessage.Should().Contain("Paused execution lost in-memory debug state");
    }

    [Fact]
    public async Task SingleNode_CancelsRunningSteps_UnderRecoveredExecutions()
    {
        var wf = SeedWorkflow();
        var execId = AddExecution(wf.Id, ExecutionStatus.Running);
        var runningStep = Guid.NewGuid();
        var finishedStep = Guid.NewGuid();
        _db.StepExecutions.Add(new StepExecution
        {
            Id = runningStep,
            WorkflowExecutionId = execId,
            StepId = "s1",
            StepType = "runScript",
            Status = ExecutionStatus.Running,
            StartedAt = DateTime.UtcNow.AddMinutes(-4)
        });
        _db.StepExecutions.Add(new StepExecution
        {
            Id = finishedStep,
            WorkflowExecutionId = execId,
            StepId = "s0",
            StepType = "runScript",
            Status = ExecutionStatus.Succeeded,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow.AddMinutes(-4)
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();

        await StartupRecovery.RecoverOrphanedExecutionsAsync(_db, NullLogger.Instance);

        var running = (await _db.StepExecutions.FindAsync(runningStep))!;
        running.Status.Should().Be(ExecutionStatus.Cancelled);
        running.ErrorOutput.Should().Contain("Step orphaned by API restart");
        (await _db.StepExecutions.FindAsync(finishedStep))!.Status.Should().Be(ExecutionStatus.Succeeded,
            "already-finished steps must not be touched");
    }

    [Fact]
    public async Task SingleNode_LeavesTerminalRowsUntouched_AndReturnsZeroWhenEmpty()
    {
        var wf = SeedWorkflow();
        var succeeded = AddExecution(wf.Id, ExecutionStatus.Succeeded);
        var failed = AddExecution(wf.Id, ExecutionStatus.Failed);
        var cancelled = AddExecution(wf.Id, ExecutionStatus.Cancelled);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();

        var recovered = await StartupRecovery.RecoverOrphanedExecutionsAsync(_db, NullLogger.Instance);

        recovered.Should().Be(0);
        (await _db.WorkflowExecutions.FindAsync(succeeded))!.Status.Should().Be(ExecutionStatus.Succeeded);
        (await _db.WorkflowExecutions.FindAsync(failed))!.Status.Should().Be(ExecutionStatus.Failed);
        (await _db.WorkflowExecutions.FindAsync(cancelled))!.Status.Should().Be(ExecutionStatus.Cancelled);
    }

    private Guid AddExecution(Guid workflowId, ExecutionStatus status)
    {
        var id = Guid.NewGuid();
        _db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = id,
            WorkflowId = workflowId,
            Status = status,
            StartedAt = DateTime.UtcNow.AddMinutes(-5)
        });
        return id;
    }
}
