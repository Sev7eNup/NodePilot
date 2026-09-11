using System.Data.Common;
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
using NodePilot.Engine.Security;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public sealed class DashboardFailureCausesTests
{
    private static readonly DateTime Now = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);

    private static WorkflowExecution Add(NodePilotDbContext db, string? message, int minutesAgo = 1, Workflow? workflow = null)
    {
        workflow ??= new Workflow { Id = Guid.NewGuid(), Name = "Workflow", DefinitionJson = "{}" };
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(), Workflow = workflow, ErrorMessage = message,
            Status = ExecutionStatus.Failed, StartedAt = Now.AddMinutes(-minutesAgo), CompletedAt = Now,
        };
        db.WorkflowExecutions.Add(execution);
        return execution;
    }

    private static Task<FailureCausesResponse> Read(NodePilotDbContext db) =>
        new DashboardFailureCauses(db, new OutputRedactor()).ReadAsync(db.WorkflowExecutions.AsNoTracking(), Now.AddDays(-1), Now, default);

    [Theory]
    [InlineData("Failure 12345678-1234-1234-abcd-123456789abc at 2026-09-11T10:15:30.1234567Z", "Failure <id> at <timestamp>")]
    [InlineData("At 2026-09-11T10:15:30+02:00\r\n  timeout 500 on server-A C:\\logs\\run.txt", "At <timestamp> timeout 500 on server-A C:\\logs\\run.txt")]
    [InlineData("code 401 vs 403 on server-B 2026-09-11", "code 401 vs 403 on server-B 2026-09-11")]
    [InlineData("password=secret-value; failed", "password=***; failed")]
    [InlineData("At 2026-09-11T10:15:30Z.", "At <timestamp>.")]
    [InlineData(" \r\n\t ", null)]
    public void Normalize_PreservesContextAndRedactsSecrets(string text, string? expected)
    {
        using var db = TestDbFactory.Create();
        new DashboardFailureCauses(db, new OutputRedactor()).Normalize(text).Should().Be(expected);
    }

    [Fact]
    public async Task Read_MergesAcrossWorkflowsBeforeRankingAndSelectsLatestExample()
    {
        using var db = TestDbFactory.Create();
        for (var i = 0; i < 12; i++) Add(db, $"Request {Guid.NewGuid()} at 2026-09-10T12:00:00Z failed", 20 + i);
        var latest = Add(db, $"Request {Guid.NewGuid()} at 2026-09-11T11:59:00Z failed");
        for (var i = 0; i < 6; i++) Add(db, $"Distinct {i}", 15);
        await db.SaveChangesAsync();
        var result = await Read(db);
        result.TotalFailed.Should().Be(19);
        result.Groups.Should().HaveCount(5);
        result.Groups[0].Should().Be(new FailureCause("Request <id> at <timestamp> failed", 13, latest.Id, latest.StartedAt));
        result.Groups.Skip(1).Select(g => g.Message).Should().Equal("Distinct 0", "Distinct 1", "Distinct 2", "Distinct 3");
        result.RemainingCount.Should().Be(2);
    }

    [Fact]
    public async Task Read_UsesFirstFailedActivityOnceAndFallsBackToExecutionError()
    {
        using var db = TestDbFactory.Create();
        var execution = Add(db, "Activity wrapper");
        execution.Steps.Add(new StepExecution { Id = Guid.Parse("00000001-0000-0000-0000-000000000000"), Status = ExecutionStatus.Failed, StartedAt = Now.AddSeconds(-30), ErrorOutput = "First cause" });
        execution.Steps.Add(new StepExecution { Id = Guid.Parse("00000002-0000-0000-0000-000000000000"), Status = ExecutionStatus.Failed, StartedAt = Now.AddSeconds(-30), ErrorOutput = "Second cause" });
        execution.Steps.Add(new StepExecution { Id = Guid.NewGuid(), Status = ExecutionStatus.Succeeded, StartedAt = Now.AddMinutes(-1), ErrorOutput = "Ignored" });
        var fallback = Add(db, "Fallback");
        fallback.Steps.Add(new StepExecution { Id = Guid.NewGuid(), Status = ExecutionStatus.Failed, ErrorOutput = " \t\r\n " });
        Add(db, null);
        Add(db, " \n ");
        await db.SaveChangesAsync();
        var result = await Read(db);
        result.TotalFailed.Should().Be(4);
        result.Groups.Should().Contain(g => g.Message == "First cause" && g.Count == 1);
        result.Groups.Should().Contain(g => g.Message == "Fallback" && g.Count == 1);
        result.Groups.Should().Contain(g => g.Message == null && g.Count == 2);
        result.Groups.Should().NotContain(g => g.Message == "Second cause" || g.Message == "Activity wrapper");
    }

    [Fact]
    public async Task Read_ExcludesOtherStatusesAndDatesOutsideWindow()
    {
        using var db = TestDbFactory.Create();
        Add(db, "At lower boundary", 1440);
        Add(db, "At upper boundary", 0);
        Add(db, "Too old", 1441);
        Add(db, "Future", -1);
        foreach (var status in Enum.GetValues<ExecutionStatus>().Where(s => s != ExecutionStatus.Failed))
            Add(db, "Not failed").Status = status;
        await db.SaveChangesAsync();
        var result = await Read(db);
        result.TotalFailed.Should().Be(2);
        result.Groups[0].Message.Should().Be("At upper boundary");
    }

    [Fact]
    public async Task Read_DoesNotMergeDifferentHostsPathsCodesOrCaseButMergesRedactedSecrets()
    {
        using var db = TestDbFactory.Create();
        foreach (var message in new[] { "server-A /a 401", "server-B /a 401", "server-A /b 401", "server-A /a 403", "Server-A /a 401", "password=one", "password=two" })
            Add(db, message);
        await db.SaveChangesAsync();
        var result = await Read(db);
        result.Groups[0].Message.Should().Be("password=***");
        result.Groups[0].Count.Should().Be(2);
        result.RemainingCount.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Endpoint_RespectsFolderAccessIncludingNoAccess(bool hasAccess)
    {
        using var db = TestDbFactory.Create();
        var allowed = new SharedWorkflowFolder { Id = Guid.NewGuid(), Name = "Allowed" };
        var hidden = new SharedWorkflowFolder { Id = Guid.NewGuid(), Name = "Hidden" };
        db.SharedWorkflowFolders.AddRange(allowed, hidden);
        Add(db, "Visible", workflow: new Workflow { Id = Guid.NewGuid(), Name = "Visible", FolderId = allowed.Id, DefinitionJson = "{}" }).StartedAt = DateTime.UtcNow.AddMinutes(-1);
        Add(db, "Hidden secret", workflow: new Workflow { Id = Guid.NewGuid(), Name = "Hidden", FolderId = hidden.Id, DefinitionJson = "{}" }).StartedAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        var authz = new Mock<IResourceAuthorizationService>();
        authz.Setup(a => a.GetAccessibleFolderIdsAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(hasAccess ? new AccessibleFolderSet { FolderIds = [allowed.Id] } : AccessibleFolderSet.None);
        var controller = new DashboardController(db, authz.Object) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var result = (FailureCausesResponse)((OkObjectResult)(await controller.GetFailureCauses(default)).Result!).Value!;
        result.TotalFailed.Should().Be(hasAccess ? 1 : 0);
        result.Groups.Should().NotContain(g => g.Message == "Hidden secret");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(24, 2)]
    [InlineData(168, 3)]
    [InlineData(720, 4)]
    [InlineData(0, 2)]
    [InlineData(721, 2)]
    public async Task Endpoint_UsesSelectedWindowAndDefaultsInvalidWindows(int hours, int expected)
    {
        using var db = TestDbFactory.Create();
        foreach (var ageHours in new[] { 0.5, 12, 100, 500, 800 })
            Add(db, "Failure").StartedAt = DateTime.UtcNow.AddHours(-ageHours);
        await db.SaveChangesAsync();
        var controller = new DashboardController(db, new AlwaysAllowAuthorizationService())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        var result = (FailureCausesResponse)((OkObjectResult)(await controller.GetFailureCauses(default, hours)).Result!).Value!;
        result.TotalFailed.Should().Be(expected);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public void Query_TranslatesWithDatabaseAggregation(string provider)
    {
        var options = new DbContextOptionsBuilder<NodePilotDbContext>();
        if (provider == "postgres") options.UseNpgsql("Host=localhost;Database=translation_only;Username=test;Password=test");
        else options.UseSqlServer("Server=(local);Database=translation_only;Integrated Security=true;TrustServerCertificate=true");
        using var db = new NodePilotDbContext(options.Options);
        var sql = new DashboardFailureCauses(db, new OutputRedactor()).BuildQuery(db.WorkflowExecutions, Now.AddDays(-1), Now).ToQueryString();
        sql.ToUpperInvariant().Should().Contain("GROUP BY").And.Contain("COUNT(").And.Contain("ROW_NUMBER()");
        if (provider == "sqlserver") sql.Should().Contain("Latin1_General_100_BIN2");
    }

    private sealed class QueryCounter : DbCommandInterceptor
    {
        public int Reads { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Reads++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task Read_RepeatedFailuresUseOneAggregatedQuery()
    {
        var (connection, seed) = TestDbFactory.CreateWithConnection();
        await using var ownedConnection = connection;
        await using var ownedSeed = seed;
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "Load", DefinitionJson = "{}" };
        for (var i = 0; i < 1000; i++) Add(seed, "Same failure", workflow: workflow);
        await seed.SaveChangesAsync();
        var counter = new QueryCounter();
        await using var db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(connection).AddInterceptors(counter).Options);
        var result = await Read(db);
        result.Groups.Should().ContainSingle().Which.Count.Should().Be(1000);
        counter.Reads.Should().Be(1);
    }
}
