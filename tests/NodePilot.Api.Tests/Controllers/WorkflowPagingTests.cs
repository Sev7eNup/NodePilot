using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Dtos;
using NodePilot.Api.Security;
using NodePilot.Core.Clients;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public sealed class WorkflowPagingTests
{
    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public void AllSortsTranslateWithRealProductionProviders_WithoutConnecting(string provider)
    {
        var options = new DbContextOptionsBuilder<NodePilotDbContext>();
        if (provider == "postgres") options.UseNpgsql("Host=localhost;Database=translation_only;Username=test;Password=test");
        else options.UseSqlServer("Server=(local);Database=translation_only;Integrated Security=true;TrustServerCertificate=true");
        using var db = new NodePilotDbContext(options.Options);
        var folderId = Guid.NewGuid();
        var authorized = db.Workflows.AsNoTracking().Where(w => w.FolderId == folderId && w.Name.ToLower().Contains("search"));
        foreach (var sort in new[] { "name", "activities", "triggers", "status", "lastRun", "successRate", "runtime", "created", "updated" })
        foreach (var ascending in new[] { true, false })
        {
            var sql = NodePilot.Api.Services.WorkflowListOrdering.Apply(db, authorized, sort, ascending)
                .Skip(50).Take(50).Select(w => new { w.Id, w.Name }).ToQueryString();
            sql.Should().Contain("ORDER BY").And.Contain("FolderId");
            if (sort is "lastRun" or "successRate" or "runtime")
                sql.Should().Contain("ROW_NUMBER()").And.Contain("rn <= 20");
        }
    }

    [Fact]
    public async Task FolderAndSearchApplyBeforePaging_AndOlderRowsRemainReachable()
    {
        using var db = TestDbFactory.Create();
        var folder = new SharedWorkflowFolder { Id = Guid.NewGuid(), Name = "Archive", Path = "/Archive", ParentFolderId = SharedWorkflowFolder.RootFolderId };
        db.SharedWorkflowFolders.Add(folder);
        var now = DateTime.UtcNow;
        db.Workflows.AddRange(Enumerable.Range(0, 525).Select(i => new Workflow
        {
            Id = Guid.NewGuid(), Name = $"Workflow{i:D4}", Description = i == 524 ? "Old required job" : null,
            UpdatedAt = now.AddSeconds(-i), FolderId = i >= 520 ? folder.Id : SharedWorkflowFolder.RootFolderId,
        }));
        await db.SaveChangesAsync();
        var controller = WorkflowControllerHarnessFactory.Build(db).Workflows;
        var last = Page(await controller.GetPaged(page: 11, pageSize: 50));
        last.Total.Should().Be(525);
        last.Items.Should().HaveCount(25).And.Contain(w => w.Name == "Workflow0524");
        var selected = Page(await controller.GetPaged(folderId: folder.Id, search: "required", pageSize: 1));
        selected.Total.Should().Be(1);
        selected.Items.Should().ContainSingle().Which.Name.Should().Be("Workflow0524");
    }

    [Theory]
    [InlineData("name")]
    [InlineData("activities")]
    [InlineData("triggers")]
    [InlineData("status")]
    [InlineData("lastRun")]
    [InlineData("successRate")]
    [InlineData("runtime")]
    [InlineData("created")]
    [InlineData("updated")]
    public async Task EverySortOrdersTheCompleteSetBeforePaging(string sort)
    {
        using var db = TestDbFactory.Create();
        var now = DateTime.UtcNow;
        var first = new Workflow { Id = Guid.NewGuid(), Name = "Alpha", ActivityCount = 1, IsEnabled = true,
            TriggerTypesJson = "[]", CreatedAt = now.AddDays(-2), UpdatedAt = now.AddDays(-2) };
        var second = new Workflow { Id = Guid.NewGuid(), Name = "Beta", ActivityCount = 2, IsEnabled = false,
            TriggerTypesJson = "[\"manualTrigger\"]", CreatedAt = now.AddDays(-1), UpdatedAt = now.AddDays(-1) };
        db.Workflows.AddRange(first, second);
        db.WorkflowExecutions.AddRange(
            new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = first.Id, StartedAt = now.AddHours(-2), CompletedAt = now.AddHours(-2).AddSeconds(1), Status = ExecutionStatus.Succeeded },
            new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = first.Id, StartedAt = now.AddHours(-3), CompletedAt = now.AddHours(-3).AddSeconds(1), Status = ExecutionStatus.Failed },
            new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = second.Id, StartedAt = now.AddHours(-1), CompletedAt = now.AddHours(-1).AddSeconds(2), Status = ExecutionStatus.Succeeded });
        await db.SaveChangesAsync();
        var controller = WorkflowControllerHarnessFactory.Build(db).Workflows;
        var ascending = Page(await controller.GetPaged(pageSize: 1, sortBy: sort, sortDir: "asc"));
        ascending.Items.Should().ContainSingle().Which.Id.Should().Be(first.Id);
        var descending = Page(await controller.GetPaged(pageSize: 1, sortBy: sort, sortDir: "desc"));
        descending.Items.Should().ContainSingle().Which.Id.Should().Be(second.Id);
        ascending.Total.Should().Be(2);
    }

    [Fact]
    public async Task ScopeAppliesToRowsAndTotal_AndPageBoundsAreSafe()
    {
        using var db = TestDbFactory.Create();
        db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "Hidden" });
        await db.SaveChangesAsync();
        var controller = WorkflowControllerHarnessFactory.Build(db, role: "Operator", userId: Guid.NewGuid(),
            authz: new ResourceAuthorizationService(db)).Workflows;
        var denied = Page(await controller.GetPaged(page: int.MaxValue, pageSize: int.MaxValue));
        denied.Items.Should().BeEmpty();
        denied.Total.Should().Be(0);
        denied.PageSize.Should().Be(200);
        var admin = WorkflowControllerHarnessFactory.Build(db).Workflows;
        var empty = Page(await admin.GetPaged(page: int.MaxValue, pageSize: int.MaxValue));
        empty.Items.Should().BeEmpty();
        empty.Total.Should().Be(1);
    }

    private static PagedResponse<WorkflowListItemResponse> Page(ActionResult<PagedResponse<WorkflowListItemResponse>> result)
        => result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should()
            .BeOfType<PagedResponse<WorkflowListItemResponse>>().Subject;

    [Fact]
    public async Task RecentIdBatchIsBounded_AndStillRespectsScopeAndSearch()
    {
        using var db = TestDbFactory.Create();
        var wanted = new Workflow { Id = Guid.NewGuid(), Name = "Recent wanted" };
        db.Workflows.AddRange(wanted, new Workflow { Id = Guid.NewGuid(), Name = "Other" });
        await db.SaveChangesAsync();
        var admin = WorkflowControllerHarnessFactory.Build(db).Workflows;
        Page(await admin.GetPaged(ids: [wanted.Id])).Items.Should().ContainSingle().Which.Id.Should().Be(wanted.Id);
        Page(await admin.GetPaged(ids: [wanted.Id], search: "no match")).Total.Should().Be(0);
        (await admin.GetPaged(ids: Enumerable.Range(0, 11).Select(_ => Guid.NewGuid()).ToArray())).Result
            .Should().BeOfType<BadRequestObjectResult>();
        var denied = WorkflowControllerHarnessFactory.Build(db, role: "Operator", userId: Guid.NewGuid(),
            authz: new ResourceAuthorizationService(db)).Workflows;
        Page(await denied.GetPaged(ids: [wanted.Id])).Total.Should().Be(0);
    }

    [Theory]
    [InlineData("lastRun")]
    [InlineData("successRate")]
    [InlineData("runtime")]
    public async Task StatisticSortRetainsNeverRunRows_AndUsesOnlyLastTwentyExecutions(string sort)
    {
        using var db = TestDbFactory.Create();
        var never = new Workflow { Id = Guid.NewGuid(), Name = "Never" };
        var run = new Workflow { Id = Guid.NewGuid(), Name = "Run" };
        db.Workflows.AddRange(never, run);
        var now = DateTime.UtcNow;
        db.WorkflowExecutions.AddRange(Enumerable.Range(0, 21).Select(i => new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = run.Id, StartedAt = now.AddHours(-i),
            CompletedAt = now.AddHours(-i).AddSeconds(i == 20 ? 100 : 1),
            Status = i == 20 ? ExecutionStatus.Failed : ExecutionStatus.Succeeded,
        }));
        await db.SaveChangesAsync();
        var controller = WorkflowControllerHarnessFactory.Build(db).Workflows;
        Page(await controller.GetPaged(pageSize: 1, sortBy: sort, sortDir: "asc")).Items
            .Should().ContainSingle().Which.Id.Should().Be(never.Id);
        var row = Page(await controller.GetPaged(pageSize: 1, sortBy: sort, sortDir: "desc")).Items.Single();
        row.Id.Should().Be(run.Id);
        row.SuccessCount.Should().Be(20);
        row.TotalCount.Should().Be(20);
        row.AvgDurationMs.Should().Be(1000);
    }
}
