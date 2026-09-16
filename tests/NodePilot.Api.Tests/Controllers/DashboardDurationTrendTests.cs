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

    // The query is written per SQL dialect. Each scenario below runs on SQLite as a unit test and,
    // through Provider_MatchesTheSqliteScenarios, on real PostgreSQL and SQL Server.

    private static async Task MedianAndNearestRankP95(NodePilotDbContext db)
    {
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

    private static async Task OnlyValidCompletedRunsInsideWindow(NodePilotDbContext db)
    {
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

    private static async Task WindowAndWorkflowFilter(NodePilotDbContext db, int hours, int count)
    {
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

    private static async Task FolderScopeProtectsSeriesAndNames(NodePilotDbContext db)
    {
        var allowed = new SharedWorkflowFolder { Id = Guid.NewGuid(), Name = "Allowed", ParentFolderId = SharedWorkflowFolder.RootFolderId };
        var hidden = new SharedWorkflowFolder { Id = Guid.NewGuid(), Name = "Hidden", ParentFolderId = SharedWorkflowFolder.RootFolderId };
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

    private static async Task OnSqlite(Func<NodePilotDbContext, Task> scenario)
    {
        using var db = TestDbFactory.Create();
        await scenario(db);
    }

    [Fact]
    public Task CalculatesMedianAndNearestRankP95AcrossRunsAndLeavesGaps() => OnSqlite(MedianAndNearestRankP95);

    [Fact]
    public Task CountsOnlyValidCompletedSuccessesAndFailuresInsideWindow() => OnSqlite(OnlyValidCompletedRunsInsideWindow);

    [Theory]
    [InlineData(1, 12)]
    [InlineData(24, 24)]
    [InlineData(168, 24)]
    [InlineData(720, 24)]
    public Task WindowsUseAllSamplesAndFilterByWorkflow(int hours, int count)
        => OnSqlite(db => WindowAndWorkflowFilter(db, hours, count));

    [Fact]
    public Task FolderScopeProtectsBothSeriesAndWorkflowNames() => OnSqlite(FolderScopeProtectsSeriesAndNames);

    [Theory]
    [Trait("Category", "DatabaseIntegration")]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public async Task Provider_MatchesTheSqliteScenarios(string provider)
    {
        if (!ProviderTestDatabase.IsConfigured(provider)) Assert.Skip($"No isolated {provider} test server configured.");
        var scenarios = new List<Func<NodePilotDbContext, Task>>
        {
            MedianAndNearestRankP95,
            OnlyValidCompletedRunsInsideWindow,
            FolderScopeProtectsSeriesAndNames,
        };
        foreach (var (hours, count) in new[] { (1, 12), (24, 24), (168, 24), (720, 24) })
            scenarios.Add(db => WindowAndWorkflowFilter(db, hours, count));

        foreach (var scenario in scenarios)
        {
            // One database per scenario, so seeded rows never leak into the next assertion.
            await using var database = await ProviderTestDatabase.CreateAsync(provider);
            await using var db = database.CreateContext();
            await scenario(db);
        }
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
