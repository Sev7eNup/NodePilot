using System.Data.Common;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

public sealed class DashboardRetryStatsTests
{
    private static readonly DateTime Now = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WorkflowExecution Add(NodePilotDbContext db, ExecutionStatus status, DateTime startedAt,
        int[] attempts, Workflow? workflow = null)
    {
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(), Status = status, StartedAt = startedAt,
            Workflow = workflow ?? new Workflow { Id = Guid.NewGuid(), Name = "Retry test", DefinitionJson = "{}", TriggerTypesJson = "[]" },
        };
        foreach (var count in attempts)
            execution.Steps.Add(new StepExecution { Id = Guid.NewGuid(), StepId = Guid.NewGuid().ToString(), AttemptCount = count });
        db.WorkflowExecutions.Add(execution);
        return execution;
    }

    private static async Task<ExecutionRetryStats> Read(NodePilotDbContext db) =>
        await DashboardRetryStats.BuildQuery(db.WorkflowExecutions.AsNoTracking(), db.StepExecutions.AsNoTracking(), Now.AddDays(-1), Now)
            .SingleOrDefaultAsync(Ct) ?? new ExecutionRetryStats(0, 0);

    [Fact]
    public async Task Query_CountsFinishedExecutionsOnceRegardlessOfActivitiesOrAttemptCount()
    {
        using var db = TestDbFactory.Create();
        foreach (var status in new[] { ExecutionStatus.Succeeded, ExecutionStatus.Failed, ExecutionStatus.Cancelled })
        {
            Add(db, status, Now, [2, 4, 1]);
            Add(db, status, Now, [1, 1]);
        }
        Add(db, ExecutionStatus.Succeeded, Now, []).TriggeredBy = $"retry:{Guid.NewGuid()}";
        foreach (var status in new[] { ExecutionStatus.Pending, ExecutionStatus.Running, ExecutionStatus.Paused, ExecutionStatus.Skipped })
            Add(db, status, Now, [3]);
        await db.SaveChangesAsync(Ct);
        (await Read(db)).Should().Be(new ExecutionRetryStats(7, 3));
    }

    [Fact]
    public async Task Query_UsesInclusiveStartTimeWindowAndExcludesFutureAndOldExecutions()
    {
        using var db = TestDbFactory.Create();
        Add(db, ExecutionStatus.Succeeded, Now.AddDays(-1), [2]);
        Add(db, ExecutionStatus.Failed, Now, [2]);
        Add(db, ExecutionStatus.Cancelled, Now.AddDays(-1).AddTicks(-1), [3]);
        Add(db, ExecutionStatus.Succeeded, Now.AddTicks(1), [3]);
        await db.SaveChangesAsync(Ct);
        (await Read(db)).Should().Be(new ExecutionRetryStats(2, 2));
    }

    [Fact]
    public async Task Query_EmptyAndNoRetriesHaveDistinctDenominators()
    {
        using var db = TestDbFactory.Create();
        (await Read(db)).Should().Be(new ExecutionRetryStats(0, 0));
        Add(db, ExecutionStatus.Succeeded, Now, [1]);
        await db.SaveChangesAsync(Ct);
        (await Read(db)).Should().Be(new ExecutionRetryStats(1, 0));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(24, 2)]
    [InlineData(168, 3)]
    [InlineData(720, 4)]
    [InlineData(0, 2)]
    [InlineData(721, 2)]
    public async Task Endpoint_ReportsRetriesForSelectedWindow(int hours, int expected)
    {
        using var db = TestDbFactory.Create();
        foreach (var age in new[] { 0.5, 12, 100, 500, 800 })
            Add(db, ExecutionStatus.Succeeded, DateTime.UtcNow.AddHours(-age), [2]);
        await db.SaveChangesAsync(Ct);
        var controller = new DashboardController(db, new AlwaysAllowAuthorizationService())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        var result = (DashboardStats)((OkObjectResult)(await controller.Get(Ct, hours)).Result!).Value!;
        result.RetryStats.Should().Be(new ExecutionRetryStats(expected, expected));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Endpoint_BothCountsRespectFolderAccessIncludingNone(bool hasAccess)
    {
        using var db = TestDbFactory.Create();
        var allowed = new SharedWorkflowFolder { Id = Guid.NewGuid(), Name = "Allowed" };
        var hidden = new SharedWorkflowFolder { Id = Guid.NewGuid(), Name = "Hidden" };
        db.SharedWorkflowFolders.AddRange(allowed, hidden);
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "Allowed", FolderId = allowed.Id, DefinitionJson = "{}", TriggerTypesJson = "[]" };
        Add(db, ExecutionStatus.Succeeded, DateTime.UtcNow.AddMinutes(-1), [2], workflow);
        Add(db, ExecutionStatus.Succeeded, DateTime.UtcNow.AddMinutes(-1), [1], workflow);
        Add(db, ExecutionStatus.Succeeded, DateTime.UtcNow.AddMinutes(-1), [2],
            new Workflow { Id = Guid.NewGuid(), Name = "Hidden", FolderId = hidden.Id, DefinitionJson = "{}", TriggerTypesJson = "[]" });
        await db.SaveChangesAsync(Ct);
        var authz = new Mock<IResourceAuthorizationService>();
        authz.Setup(a => a.GetAccessibleFolderIdsAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(hasAccess ? new AccessibleFolderSet { FolderIds = [allowed.Id] } : AccessibleFolderSet.None);
        var controller = new DashboardController(db, authz.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        var result = (DashboardStats)((OkObjectResult)(await controller.Get(Ct)).Result!).Value!;
        result.RetryStats.Should().Be(hasAccess ? new ExecutionRetryStats(2, 1) : new ExecutionRetryStats(0, 0));
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public void Query_TranslatesToOneAggregateOverDeduplicatedExecutionIds(string provider)
    {
        var options = new DbContextOptionsBuilder<NodePilotDbContext>();
        if (provider == "postgres") options.UseNpgsql("Host=localhost;Database=translation_only;Username=test;Password=test");
        else options.UseSqlServer("Server=(local);Database=translation_only;Integrated Security=true;TrustServerCertificate=true");
        using var db = new NodePilotDbContext(options.Options);
        var sql = DashboardRetryStats.BuildQuery(db.WorkflowExecutions, db.StepExecutions, Now.AddDays(-1), Now).ToQueryString();
        sql.ToUpperInvariant().Should().Contain("COUNT(").And.Contain("DISTINCT").And.Contain("LEFT JOIN").And.Contain("GROUP BY");
    }

    private sealed class QueryCounter : DbCommandInterceptor
    {
        public int Reads { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Reads++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task Query_ManyExecutionsAndActivitiesStillUseOneDatabaseRead()
    {
        var (connection, seed) = TestDbFactory.CreateWithConnection();
        await using var ownedConnection = connection;
        await using var ownedSeed = seed;
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "Load", DefinitionJson = "{}" };
        for (var i = 0; i < 1000; i++) Add(seed, ExecutionStatus.Succeeded, Now, i % 2 == 0 ? [1, 2, 3] : [1, 1, 1], workflow);
        await seed.SaveChangesAsync(Ct);
        var counter = new QueryCounter();
        await using var db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(connection).AddInterceptors(counter).Options);
        (await Read(db)).Should().Be(new ExecutionRetryStats(1000, 500));
        counter.Reads.Should().Be(1);
    }
}
