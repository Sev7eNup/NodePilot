using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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

/// <summary>
/// Regression tests for the "deleted edge still runs downstream" bug. Roots are trigger-ONLY:
/// only trigger nodes are entry points. Orphaned activities (inDegree 0 but not a trigger) are
/// skipped, so a user-deleted incoming edge no longer silently promotes its target to an entry
/// point. A workflow without any (enabled) trigger node has NO root → the execution fails.
/// </summary>
[Collection("SerialEngineTests")]
public class WorkflowEngineTriggerRootTests
{
    private readonly NodePilotDbContext _db;
    private readonly IServiceProvider _serviceProvider;
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly Mock<IActivityExecutor> _runScriptExecutor;
    private readonly Mock<IActivityExecutor> _manualTriggerExecutor;
    private readonly Mock<IActivityExecutor> _scheduleTriggerExecutor;
    private readonly WorkflowEngine _engine;

    public WorkflowEngineTriggerRootTests()
    {
        _runScriptExecutor = new Mock<IActivityExecutor>();
        _runScriptExecutor.Setup(e => e.ActivityType).Returns("runScript");
        _runScriptExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult { Success = true, Output = "OK" });

        _manualTriggerExecutor = new Mock<IActivityExecutor>();
        _manualTriggerExecutor.Setup(e => e.ActivityType).Returns("manualTrigger");
        _manualTriggerExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult { Success = true, Output = "{}" });

        // Roots are EVERY trigger type, not just manualTrigger — a scheduleTrigger node is just as
        // valid an entry point. Registered so the schedule-rooted tests can run it.
        _scheduleTriggerExecutor = new Mock<IActivityExecutor>();
        _scheduleTriggerExecutor.Setup(e => e.ActivityType).Returns("scheduleTrigger");
        _scheduleTriggerExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult { Success = true, Output = "{}" });

        var registry = new ActivityRegistry(new[] { _runScriptExecutor.Object, _manualTriggerExecutor.Object, _scheduleTriggerExecutor.Object });
        (_db, var sp, _connection) = TestDbContext.CreateWithScopedServices(registry);
        _serviceProvider = sp;

        var notifier = new Mock<IExecutionNotifier>();
        _engine = new WorkflowEngine(_db, registry, NullLogger<WorkflowEngine>.Instance, _serviceProvider, notifier.Object);
    }

    private static Workflow CreateWorkflow(string definitionJson) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test",
        DefinitionJson = definitionJson
    };

    [Fact]
    public async Task ExecuteAsync_OrphanNonTriggerNode_IsSkippedWhenGraphHasTrigger()
    {
        // Graph: manualTrigger -> step-1. Isolated "step-orphan" (runScript, no incoming edge).
        // Engine should run trigger + step-1, mark step-orphan as Skipped — NOT execute it.
        // This models the bug reproduction: user deleted the edge feeding step-orphan.
        const string json = """
        {
          "nodes": [
            {"id":"trigger-1","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"manualTrigger","config":{}}},
            {"id":"step-1","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{}}},
            {"id":"step-orphan","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{}}}
          ],
          "edges": [
            {"id":"e1","source":"trigger-1","target":"step-1"}
          ]
        }
        """;
        var workflow = CreateWorkflow(json);
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Succeeded);

        var steps = _db.StepExecutions.Where(s => s.WorkflowExecutionId == execution.Id).ToList();
        var orphan = steps.SingleOrDefault(s => s.StepId == "step-orphan");
        orphan.Should().NotBeNull("orphan node should still produce a step record");
        orphan!.Status.Should().Be(ExecutionStatus.Skipped);

        var runStep = steps.SingleOrDefault(s => s.StepId == "step-1");
        runStep!.Status.Should().Be(ExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task ExecuteAsync_OrphanWithDownstreamChain_WholeChainIsSkipped()
    {
        // Graph: manualTrigger -> step-1. Orphan chain: step-A -> step-B. Both step-A and step-B
        // must be skipped — otherwise the user's "deleted edge" scenario would still run the tail.
        const string json = """
        {
          "nodes": [
            {"id":"trigger-1","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"manualTrigger","config":{}}},
            {"id":"step-1","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{}}},
            {"id":"step-A","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{}}},
            {"id":"step-B","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{}}}
          ],
          "edges": [
            {"id":"e1","source":"trigger-1","target":"step-1"},
            {"id":"e2","source":"step-A","target":"step-B"}
          ]
        }
        """;
        var workflow = CreateWorkflow(json);
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Succeeded);

        var steps = _db.StepExecutions.Where(s => s.WorkflowExecutionId == execution.Id).ToList();
        steps.Single(s => s.StepId == "step-A").Status.Should().Be(ExecutionStatus.Skipped);
        steps.Single(s => s.StepId == "step-B").Status.Should().Be(ExecutionStatus.Skipped);
        steps.Single(s => s.StepId == "step-1").Status.Should().Be(ExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task ExecuteAsync_WorkflowWithoutTrigger_FailsWithNoRootNodes()
    {
        // No trigger anywhere: step-1 -> step-2. Roots are trigger-only, so the graph has NO
        // entry point — the engine must fail the execution (and run NO steps) instead of
        // promoting the inDegree-0 activity to a root (the removed legacy fallback).
        const string json = """
        {
          "nodes": [
            {"id":"step-1","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{}}},
            {"id":"step-2","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{}}}
          ],
          "edges": [
            {"id":"e1","source":"step-1","target":"step-2"}
          ]
        }
        """;
        var workflow = CreateWorkflow(json);
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Failed);
        execution.ErrorMessage.Should().NotBeNullOrEmpty();
        execution.ErrorMessage.Should().Contain("trigger");
        execution.CompletedAt.Should().NotBeNull();

        var steps = _db.StepExecutions.Where(s => s.WorkflowExecutionId == execution.Id).ToList();
        steps.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_NonManualTriggerRoot_RunsTheWorkflow()
    {
        // Roots are EVERY trigger type — a scheduleTrigger is a valid entry point (the node just
        // runs as a pass-through at execution time; the actual scheduling is the orchestrator's job).
        // This pins the rule that a workflow's root nodes are every trigger type, not just
        // manualTrigger.
        const string json = """
        {
          "nodes": [
            {"id":"sched","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"scheduleTrigger","config":{"cronExpression":"0 0 * * * ?"}}},
            {"id":"step-1","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{}}}
          ],
          "edges": [
            {"id":"e1","source":"sched","target":"step-1"}
          ]
        }
        """;
        var workflow = CreateWorkflow(json);
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "scheduleTrigger", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Succeeded);
        var steps = _db.StepExecutions.Where(s => s.WorkflowExecutionId == execution.Id).ToList();
        steps.Single(s => s.StepId == "sched").Status.Should().Be(ExecutionStatus.Succeeded);
        steps.Single(s => s.StepId == "step-1").Status.Should().Be(ExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task ExecuteAsync_OnlyTriggerIsDisabled_FailsWithNoRootNodes()
    {
        // The single trigger is disabled → it is NOT a root → the graph has no entry point →
        // the execution fails (a disabled trigger must never silently promote downstream to a root).
        const string json = """
        {
          "nodes": [
            {"id":"trigger-1","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"manualTrigger","disabled":true,"config":{}}},
            {"id":"step-1","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{}}}
          ],
          "edges": [
            {"id":"e1","source":"trigger-1","target":"step-1"}
          ]
        }
        """;
        var workflow = CreateWorkflow(json);
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Failed);
        execution.ErrorMessage.Should().Contain("trigger");
        _db.StepExecutions.Where(s => s.WorkflowExecutionId == execution.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_MultipleTriggers_AllFireAsRoots()
    {
        // Two enabled triggers (manual + schedule), each feeding its own branch. Both are roots →
        // both branches run. This reflects the current model, after an earlier proposal to
        // restrict callable sub-workflows to manualTrigger-only was dropped: every trigger node
        // is an entry point. (A child invoked via startWorkflow can therefore start from
        // whatever trigger it has.)
        const string json = """
        {
          "nodes": [
            {"id":"manual","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"manualTrigger","config":{}}},
            {"id":"sched","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"scheduleTrigger","config":{"cronExpression":"0 0 * * * ?"}}},
            {"id":"from-manual","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{}}},
            {"id":"from-sched","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{}}}
          ],
          "edges": [
            {"id":"e1","source":"manual","target":"from-manual"},
            {"id":"e2","source":"sched","target":"from-sched"}
          ]
        }
        """;
        var workflow = CreateWorkflow(json);
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        var execution = await _engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Succeeded);
        var steps = _db.StepExecutions.Where(s => s.WorkflowExecutionId == execution.Id).ToList();
        steps.Single(s => s.StepId == "from-manual").Status.Should().Be(ExecutionStatus.Succeeded);
        steps.Single(s => s.StepId == "from-sched").Status.Should().Be(ExecutionStatus.Succeeded);
    }
}
