using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.Tests.TestSupport;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// The sidebar renders on every page, so these three numbers used to drag the whole dashboard
/// aggregate — around twenty sequential queries including an unfiltered count over the executions
/// table — along with them. This endpoint answers the same question with three counts.
/// </summary>
public class DashboardSidebarCountsTests
{
    private static DashboardController NewController(NodePilot.Data.NodePilotDbContext db, string role = "Admin")
    {
        var controller = new DashboardController(db, new AlwaysAllowAuthorizationService());
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, role)], "TestAuth"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal },
        };
        return controller;
    }

    private static SidebarCounts Read(ActionResult<SidebarCounts> result)
        => (result.Result as OkObjectResult)!.Value.Should().BeOfType<SidebarCounts>().Subject;

    [Fact]
    public async Task GetSidebarCounts_CountsWorkflowsRunningExecutionsAndMachines()
    {
        var db = NodePilot.TestCommons.TestDbFactory.Create();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        db.Workflows.Add(wf);
        db.ManagedMachines.AddRange(
            new ManagedMachine { Id = Guid.NewGuid(), Name = "A", Hostname = "a" },
            new ManagedMachine { Id = Guid.NewGuid(), Name = "B", Hostname = "b" });
        db.WorkflowExecutions.AddRange(
            new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = wf.Id, Status = ExecutionStatus.Running, StartedAt = DateTime.UtcNow },
            new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = wf.Id, Status = ExecutionStatus.Running, StartedAt = DateTime.UtcNow },
            // Terminal runs are not "running" and must not inflate the badge.
            new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = wf.Id, Status = ExecutionStatus.Succeeded, StartedAt = DateTime.UtcNow },
            new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = wf.Id, Status = ExecutionStatus.Failed, StartedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var counts = Read(await NewController(db).GetSidebarCounts(CancellationToken.None));

        counts.WorkflowsTotal.Should().Be(1);
        counts.RunningCount.Should().Be(2);
        counts.MachinesTotal.Should().Be(2);
    }

    [Fact]
    public async Task GetSidebarCounts_EmptyDatabase_ReturnsZeros()
    {
        var db = NodePilot.TestCommons.TestDbFactory.Create();

        var counts = Read(await NewController(db).GetSidebarCounts(CancellationToken.None));

        counts.Should().Be(new SidebarCounts(0, 0, 0));
    }

    [Fact]
    public async Task GetSidebarCounts_ViewerRole_StillGetsTheCounts()
    {
        // The badges render for every role; only the alerting badge is gated, and that comes
        // from a different endpoint.
        var db = NodePilot.TestCommons.TestDbFactory.Create();
        db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" });
        await db.SaveChangesAsync();

        var counts = Read(await NewController(db, "Viewer").GetSidebarCounts(CancellationToken.None));

        counts.WorkflowsTotal.Should().Be(1);
    }
}
