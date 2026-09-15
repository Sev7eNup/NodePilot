using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.Services;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public sealed class DashboardDurationTrendTests
{
    private static readonly DateTime Now = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    private static WorkflowExecution Add(NodePilotDbContext db, Workflow workflow, double milliseconds,
        DateTime? start = null, ExecutionStatus status = ExecutionStatus.Succeeded)
    {
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(), Workflow = workflow, Status = status,
            StartedAt = start ?? Now.AddMinutes(-30),
            CompletedAt = (start ?? Now.AddMinutes(-30)).AddMilliseconds(milliseconds),
        };
        db.WorkflowExecutions.Add(execution);
        return execution;
    }

    private static Workflow Workflow(string name = "Example", Guid? folderId = null) => new()
    {
        Id = Guid.NewGuid(), Name = name, DefinitionJson = "{}",
        FolderId = folderId ?? SharedWorkflowFolder.RootFolderId,
    };

    [Fact]
    public async Task CalculatesMedianAndNearestRankP95AcrossRunsAndLeavesGaps()
    {
        using var db = TestDbFactory.Create();
        var workflow = Workflow();
        foreach (var ms in Enumerable.Range(1, 20)) Add(db, workflow, ms * 1000);
        Add(db, workflow, 0, Now.AddHours(-2));
        await db.SaveChangesAsync();
        var result = await new DashboardDurationTrend(db).ReadAsync(AccessibleFolderSet.Unrestricted, 24, null, default, Now);
        result.Buckets.Should().HaveCount(24);
        result.Buckets[^1].Should().Be(new DurationBucket(Now.AddHours(-1), 20, 10500, 19000));
        result.Buckets[^2].Should().Be(new DurationBucket(Now.AddHours(-2), 1, 0, 0));
        result.Buckets[0].Should().Be(new DurationBucket(Now.AddDays(-1), 0, null, null));
    }

    [Fact]
    public async Task CountsOnlyValidCompletedSuccessesAndFailuresInsideWindow()
    {
        using var db = TestDbFactory.Create();
        var workflow = Workflow();
        Add(db, workflow, 1000, Now.AddDays(-1));
        Add(db, workflow, 3000, status: ExecutionStatus.Failed);
        foreach (var status in Enum.GetValues<ExecutionStatus>().Where(s => s is not (ExecutionStatus.Succeeded or ExecutionStatus.Failed)))
            Add(db, workflow, 1000, status: status);
        Add(db, workflow, 1000, Now.AddDays(-1).AddMilliseconds(-1));
        Add(db, workflow, 0, Now);
        Add(db, workflow, -1);
        Add(db, workflow, 1000).CompletedAt = null;
        Add(db, workflow, 1000).CompletedAt = Now.AddSeconds(1);
        await db.SaveChangesAsync();
        var result = await new DashboardDurationTrend(db).ReadAsync(AccessibleFolderSet.Unrestricted, 24, null, default, Now);
        result.Buckets.Sum(b => b.Count).Should().Be(2);
        result.Buckets[0].MedianMs.Should().Be(1000);
        result.Buckets[^1].P95Ms.Should().Be(3000);
    }

    [Theory]
    [InlineData(1, 12)]
    [InlineData(24, 24)]
    [InlineData(168, 24)]
    [InlineData(720, 24)]
    public async Task WindowsUseAllSamplesAndFilterByWorkflow(int hours, int count)
    {
        using var db = TestDbFactory.Create();
        var selected = Workflow("Selected");
        Add(db, selected, 500, Now.AddHours(-hours));
        Add(db, selected, 1500, Now.AddMinutes(-1));
        Add(db, Workflow("Other"), 9000, Now.AddMinutes(-1));
        await db.SaveChangesAsync();
        var result = await new DashboardDurationTrend(db).ReadAsync(AccessibleFolderSet.Unrestricted, hours, selected.Id, default, Now);
        result.Buckets.Should().HaveCount(count);
        result.Buckets.Sum(b => b.Count).Should().Be(2);
        result.Buckets[0].MedianMs.Should().Be(500);
        result.Buckets[^1].MedianMs.Should().Be(1500);
        result.Workflows.Select(w => w.Name).Should().Equal("Other", "Selected");
    }

    [Fact]
    public async Task FolderScopeProtectsBothSeriesAndWorkflowNames()
    {
        using var db = TestDbFactory.Create();
        var allowed = new SharedWorkflowFolder { Id = Guid.NewGuid(), Name = "Allowed" };
        var hidden = new SharedWorkflowFolder { Id = Guid.NewGuid(), Name = "Hidden" };
        db.SharedWorkflowFolders.AddRange(allowed, hidden);
        var visible = Workflow("Visible", allowed.Id);
        var secret = Workflow("Secret", hidden.Id);
        Add(db, visible, 1000);
        Add(db, secret, 9000);
        await db.SaveChangesAsync();
        var scope = new AccessibleFolderSet { FolderIds = [allowed.Id] };
        var service = new DashboardDurationTrend(db);
        var result = await service.ReadAsync(scope, 24, null, default, Now);
        result.Workflows.Should().ContainSingle(w => w.Id == visible.Id);
        result.Buckets.Sum(b => b.Count).Should().Be(1);
        result.Buckets[^1].MedianMs.Should().Be(1000);
        foreach (var id in new[] { secret.Id, Guid.NewGuid() })
            (await service.ReadAsync(scope, 24, id, default, Now)).Buckets.Sum(b => b.Count).Should().Be(0);
        var empty = await service.ReadAsync(AccessibleFolderSet.None, 24, null, default, Now);
        empty.Workflows.Should().BeEmpty();
        empty.Buckets.Should().OnlyContain(b => b.Count == 0 && b.MedianMs == null && b.P95Ms == null);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(721)]
    public async Task EndpointDefaultsUnsupportedWindowsTo24Hours(int hours)
    {
        using var db = TestDbFactory.Create();
        var authz = new Mock<IResourceAuthorizationService>();
        authz.Setup(a => a.GetAccessibleFolderIdsAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AccessibleFolderSet.None);
        var controller = new DashboardController(db, authz.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        var response = (DurationTrendResponse)((OkObjectResult)(await controller.GetDurationTrend(default, hours)).Result!).Value!;
        response.Buckets.Should().HaveCount(24);
        (response.Buckets[1].StartedAt - response.Buckets[0].StartedAt).Should().Be(TimeSpan.FromHours(1));
    }
}
