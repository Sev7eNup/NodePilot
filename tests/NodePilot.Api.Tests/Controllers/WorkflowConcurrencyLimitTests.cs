using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Dtos;
using NodePilot.Api.Tests.TestSupport;
using NodePilot.Core.Audit;
using NodePilot.Core.Models;
using NodePilot.Data;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// The per-workflow concurrency limit endpoint. It is deliberately separate from the
/// update/publish bodies, so these tests also pin that it behaves operationally: no lock, no
/// version bump, no version-history entry.
/// </summary>
public class WorkflowConcurrencyLimitTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(1000)]
    public async Task SetConcurrencyLimit_WithValidValue_Persists(int limit)
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = await SeedAsync(db);
        var harness = WorkflowControllerHarnessFactory.Build(db);

        var result = await harness.Workflows.SetConcurrencyLimit(
            workflow.Id, new SetWorkflowConcurrencyLimitRequest(limit), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (await db.Workflows.AsNoTracking().SingleAsync()).MaxConcurrentExecutions.Should().Be(limit);
    }

    [Fact]
    public async Task SetConcurrencyLimit_WithExplicitNull_ClearsTheLimit()
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = await SeedAsync(db, limit: 5);
        var harness = WorkflowControllerHarnessFactory.Build(db);

        await harness.Workflows.SetConcurrencyLimit(
            workflow.Id, new SetWorkflowConcurrencyLimitRequest(null), CancellationToken.None);

        (await db.Workflows.AsNoTracking().SingleAsync()).MaxConcurrentExecutions.Should().BeNull();
    }

    [Fact]
    public async Task SetConcurrencyLimit_WithZero_ReturnsBadRequest()
    {
        // Zero means "unlimited" to Engine:MaxConcurrentExecutions, so it must not silently
        // mean "never run" here.
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = await SeedAsync(db, limit: 3);
        var harness = WorkflowControllerHarnessFactory.Build(db);

        var result = await harness.Workflows.SetConcurrencyLimit(
            workflow.Id, new SetWorkflowConcurrencyLimitRequest(0), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await db.Workflows.AsNoTracking().SingleAsync()).MaxConcurrentExecutions.Should().Be(3);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1001)]
    public async Task SetConcurrencyLimit_OutOfRange_ReturnsBadRequest(int limit)
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = await SeedAsync(db);
        var harness = WorkflowControllerHarnessFactory.Build(db);

        var result = await harness.Workflows.SetConcurrencyLimit(
            workflow.Id, new SetWorkflowConcurrencyLimitRequest(limit), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await db.Workflows.AsNoTracking().SingleAsync()).MaxConcurrentExecutions.Should().BeNull();
    }

    [Fact]
    public async Task SetConcurrencyLimit_PushesTheNewValueToTheGate()
    {
        // Without the push, the dispatcher's claim filter keeps skipping a saturated workflow
        // and a raised limit is never re-read.
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = await SeedAsync(db, limit: 1);
        var harness = WorkflowControllerHarnessFactory.Build(db);
        harness.Concurrency.TryAcquire(workflow.Id, 1).Should().BeTrue();
        harness.Concurrency.BlockedWorkflowIds.Should().Contain(workflow.Id);

        await harness.Workflows.SetConcurrencyLimit(
            workflow.Id, new SetWorkflowConcurrencyLimitRequest(4), CancellationToken.None);

        harness.Concurrency.BlockedWorkflowIds.Should().BeEmpty();
        harness.Concurrency.TryAcquire(workflow.Id, 1).Should().BeTrue("the pushed limit of 4 wins over a stale observation");
    }

    [Fact]
    public async Task SetConcurrencyLimit_DoesNotBumpVersionOrSnapshotHistory()
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = await SeedAsync(db);
        var harness = WorkflowControllerHarnessFactory.Build(db);

        await harness.Workflows.SetConcurrencyLimit(
            workflow.Id, new SetWorkflowConcurrencyLimitRequest(2), CancellationToken.None);

        var stored = await db.Workflows.AsNoTracking().SingleAsync();
        stored.Version.Should().Be(1);
        (await db.WorkflowVersions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SetConcurrencyLimit_WhileCheckedOutByAnotherUser_StillSucceeds()
    {
        // Operational kill-switch semantics, like Disable: throttling must not require a lock.
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = await SeedAsync(db);
        workflow.CheckedOutByUserId = Guid.NewGuid();
        workflow.CheckedOutAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        var harness = WorkflowControllerHarnessFactory.Build(db);

        var result = await harness.Workflows.SetConcurrencyLimit(
            workflow.Id, new SetWorkflowConcurrencyLimitRequest(2), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task SetConcurrencyLimit_WritesAuditEntry()
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = await SeedAsync(db);
        var audit = new CapturingAuditWriter();
        var harness = WorkflowControllerHarnessFactory.Build(db, audit);

        await harness.Workflows.SetConcurrencyLimit(
            workflow.Id, new SetWorkflowConcurrencyLimitRequest(6), CancellationToken.None);

        audit.Calls.Should().ContainSingle(e => e.Action == AuditActions.WorkflowConcurrencyLimitChanged);
    }

    [Fact]
    public async Task SetConcurrencyLimit_UnchangedValue_IsANoOpAndNotAudited()
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = await SeedAsync(db, limit: 3);
        var audit = new CapturingAuditWriter();
        var harness = WorkflowControllerHarnessFactory.Build(db, audit);

        var result = await harness.Workflows.SetConcurrencyLimit(
            workflow.Id, new SetWorkflowConcurrencyLimitRequest(3), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        audit.Calls.Should().NotContain(e => e.Action == AuditActions.WorkflowConcurrencyLimitChanged);
    }

    [Fact]
    public async Task SetConcurrencyLimit_UnknownWorkflow_ReturnsNotFound()
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var harness = WorkflowControllerHarnessFactory.Build(db);

        var result = await harness.Workflows.SetConcurrencyLimit(
            Guid.NewGuid(), new SetWorkflowConcurrencyLimitRequest(2), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Duplicate_CopiesTheConcurrencyLimit()
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = await SeedAsync(db, limit: 7);
        var harness = WorkflowControllerHarnessFactory.Build(db);

        await harness.Workflows.Duplicate(workflow.Id, CancellationToken.None);

        var copy = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id != workflow.Id);
        copy.MaxConcurrentExecutions.Should().Be(7);
        copy.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task GetById_SurfacesTheConcurrencyLimit()
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = await SeedAsync(db, limit: 9);
        var harness = WorkflowControllerHarnessFactory.Build(db);

        var result = await harness.Workflows.GetById(workflow.Id, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<WorkflowResponse>()
            .Which.MaxConcurrentExecutions.Should().Be(9);
    }

    private static async Task<Workflow> SeedAsync(NodePilotDbContext db, int? limit = null)
    {
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Limited",
            DefinitionJson = "{}",
            IsEnabled = true,
            MaxConcurrentExecutions = limit,
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        return workflow;
    }
}
