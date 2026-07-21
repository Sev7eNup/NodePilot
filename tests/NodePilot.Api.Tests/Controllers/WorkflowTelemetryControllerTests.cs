using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// Tests for the read-only telemetry endpoints (step-health / coverage / step-stats).
/// Moved from <c>ExecutionsControllerTests</c> when the endpoints were extracted into
/// <see cref="WorkflowTelemetryController"/> (2026-07 coherence cleanup).
/// </summary>
public class WorkflowTelemetryControllerTests
{
    private static NodePilotDbContext CreateContext() => NodePilot.TestCommons.TestDbFactory.Create();

    private static WorkflowTelemetryController NewController(NodePilotDbContext db)
    {
        var controller = new WorkflowTelemetryController(db, new AlwaysAllowAuthorizationService());
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin") },
                "TestAuth"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    [Fact]
    public async Task GetCoverage_AggregatesPerNodeAcrossExecutionsInWindow()
    {
        var db = CreateContext();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        db.Workflows.Add(workflow);
        // Three executions: 2 succeeded, 1 failed; all within window. step "a" runs in all,
        // step "b" only in execution-1, step "c" never runs (skipped in all three).
        var execs = Enumerable.Range(0, 3).Select(i => new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Status = i == 1 ? ExecutionStatus.Failed : ExecutionStatus.Succeeded,
            StartedAt = DateTime.UtcNow.AddDays(-i),
            CompletedAt = DateTime.UtcNow.AddDays(-i).AddSeconds(1),
        }).ToList();
        db.WorkflowExecutions.AddRange(execs);

        // step "a" → succeeded in execs[0] + execs[2], failed in execs[1]
        for (int i = 0; i < 3; i++)
        {
            db.StepExecutions.Add(new StepExecution
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = execs[i].Id,
                StepId = "a", StepType = "runScript",
                Status = i == 1 ? ExecutionStatus.Failed : ExecutionStatus.Succeeded,
                StartedAt = execs[i].StartedAt,
                CompletedAt = execs[i].StartedAt.AddSeconds(1),
            });
        }
        // step "b" only in exec[0]
        db.StepExecutions.Add(new StepExecution
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execs[0].Id,
            StepId = "b", StepType = "log",
            Status = ExecutionStatus.Succeeded,
            StartedAt = execs[0].StartedAt,
            CompletedAt = execs[0].StartedAt.AddSeconds(1),
        });
        // step "c" was skipped in all three
        for (int i = 0; i < 3; i++)
        {
            db.StepExecutions.Add(new StepExecution
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = execs[i].Id,
                StepId = "c", StepType = "delay",
                Status = ExecutionStatus.Skipped,
                StartedAt = execs[i].StartedAt,
                CompletedAt = execs[i].StartedAt.AddSeconds(1),
            });
        }
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var result = await controller.GetCoverage(workflow.Id, 30, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var resp = ok.Value.Should().BeAssignableTo<WorkflowCoverageResponse>().Subject;
        resp.TotalExecutions.Should().Be(3);
        resp.WindowDays.Should().Be(30);

        var byStep = resp.Nodes.ToDictionary(n => n.StepId);
        byStep.Should().ContainKeys("a", "b", "c");
        byStep["a"].ExecutedCount.Should().Be(3);
        byStep["a"].FailedCount.Should().Be(1);
        byStep["a"].SkippedCount.Should().Be(0);
        byStep["b"].ExecutedCount.Should().Be(1);
        byStep["b"].FailedCount.Should().Be(0);
        byStep["c"].ExecutedCount.Should().Be(0);
        byStep["c"].SkippedCount.Should().Be(3);
    }

    [Fact]
    public async Task GetCoverage_NoExecutions_ReturnsEmptyNodeListAndZeroTotal()
    {
        var db = CreateContext();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var result = await controller.GetCoverage(workflow.Id, 30, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var resp = ok.Value.Should().BeAssignableTo<WorkflowCoverageResponse>().Subject;
        resp.TotalExecutions.Should().Be(0);
        resp.Nodes.Should().BeEmpty();
        resp.OldestExecutionInWindow.Should().BeNull();
    }

    [Fact]
    public async Task GetCoverage_RespectsWindowDays_ExcludesOlderExecutions()
    {
        var db = CreateContext();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        db.Workflows.Add(workflow);
        // One run inside window, one well outside.
        var inWindow = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id,
            Status = ExecutionStatus.Succeeded,
            StartedAt = DateTime.UtcNow.AddDays(-2),
            CompletedAt = DateTime.UtcNow.AddDays(-2).AddSeconds(1),
        };
        var outOfWindow = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id,
            Status = ExecutionStatus.Succeeded,
            StartedAt = DateTime.UtcNow.AddDays(-90),
            CompletedAt = DateTime.UtcNow.AddDays(-90).AddSeconds(1),
        };
        db.WorkflowExecutions.AddRange(inWindow, outOfWindow);
        db.StepExecutions.AddRange(
            new StepExecution { Id = Guid.NewGuid(), WorkflowExecutionId = inWindow.Id, StepId = "x", StepType = "log", Status = ExecutionStatus.Succeeded, StartedAt = inWindow.StartedAt, CompletedAt = inWindow.CompletedAt },
            new StepExecution { Id = Guid.NewGuid(), WorkflowExecutionId = outOfWindow.Id, StepId = "y", StepType = "log", Status = ExecutionStatus.Succeeded, StartedAt = outOfWindow.StartedAt, CompletedAt = outOfWindow.CompletedAt });
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var result = await controller.GetCoverage(workflow.Id, 30, CancellationToken.None);

        var resp = ((OkObjectResult)result.Result!).Value as WorkflowCoverageResponse;
        resp!.TotalExecutions.Should().Be(1);
        resp.Nodes.Should().ContainSingle(n => n.StepId == "x");
        resp.Nodes.Should().NotContain(n => n.StepId == "y");
    }

    [Fact]
    public async Task GetStepStats_WithMoreThan999Executions_Returns200()
    {
        // Arrange — seed 1001 executions to exceed SQLite's SQLITE_LIMIT_VARIABLE_NUMBER=999
        var db = CreateContext();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        db.Workflows.Add(workflow);
        var executions = Enumerable.Range(0, 1001).Select(i => new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Succeeded,
            StartedAt = DateTime.UtcNow.AddDays(-1).AddSeconds(i),
            CompletedAt = DateTime.UtcNow.AddDays(-1).AddSeconds(i + 1)
        }).ToList();
        db.WorkflowExecutions.AddRange(executions);
        var step = new StepExecution
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = executions[0].Id,
            StepId = "s1",
            StepType = "log",
            Status = ExecutionStatus.Succeeded,
            StartedAt = DateTime.UtcNow.AddDays(-1),
            CompletedAt = DateTime.UtcNow.AddDays(-1).AddSeconds(1)
        };
        db.StepExecutions.Add(step);
        await db.SaveChangesAsync();

        var controller = NewController(db);

        // Act — must not throw "too many SQL variables"
        var result = await controller.GetStepStats(workflow.Id, 30, CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetStepHealth_ReturnsNewestOutcomesPerStep_CappedByLimit()
    {
        var db = CreateContext();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        db.Workflows.Add(workflow);
        // Three runs, newest first outcome pattern for step "a": Succeeded, Failed, Succeeded.
        var execs = Enumerable.Range(0, 3).Select(i => new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Succeeded,
            StartedAt = DateTime.UtcNow.AddMinutes(-i * 10),
        }).ToList();
        db.WorkflowExecutions.AddRange(execs);
        for (int i = 0; i < 3; i++)
        {
            db.StepExecutions.Add(new StepExecution
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = execs[i].Id,
                StepId = "a", StepType = "runScript",
                Status = i == 1 ? ExecutionStatus.Failed : ExecutionStatus.Succeeded,
                StartedAt = execs[i].StartedAt,
            });
        }
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var result = await controller.GetStepHealth(workflow.Id, stepIds: null, limit: 2, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var health = ok.Value.Should().BeAssignableTo<Dictionary<string, List<StepHealthEntry>>>().Subject;
        health.Should().ContainKey("a");
        health["a"].Should().HaveCount(2, "limit caps the sparkline length");
        health["a"][0].Status.Should().Be("Succeeded", "entries are ordered newest-first");
        health["a"][1].Status.Should().Be("Failed");
    }
}
