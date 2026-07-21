using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public class MaintenanceWindowsControllerTests
{
    private sealed class SingleContextScopeFactory(NodePilotDbContext db)
        : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type t) => t == typeof(NodePilotDbContext) ? db : null;
        public void Dispose() { }
    }

    private static MaintenanceWindowsController Build(NodePilotDbContext db)
    {
        var evaluator = new MaintenanceWindowEvaluator(
            new SingleContextScopeFactory(db), NullLogger<MaintenanceWindowEvaluator>.Instance);
        var ctrl = new MaintenanceWindowsController(
            new MaintenanceWindowStore(db), evaluator, db, NoopAuditWriter.Instance,
            new NodePilot.Api.Security.ResourceAuthorizationService(db));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "Admin"), new Claim(ClaimTypes.Name, "admin")], "test"));
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
        return ctrl;
    }

    private static CreateMaintenanceWindowRequest GlobalWeekly(string name) => new(
        name, null, true, "Blackout", "Global", "Weekly",
        null, null, 0b0111110, 22 * 60, 23 * 60, null, null, "UTC", null);

    private static CreateMaintenanceWindowRequest GlobalCron(string name) => new(
        name, null, true, "Blackout", "Global", "Cron",
        null, null, 0, null, null, "0 0 3 ? * SAT", 90, "UTC", null);

    [Fact]
    public async Task Create_ValidGlobalWeekly_Created()
    {
        await using var db = TestDbFactory.Create();
        var ctrl = Build(db);

        var result = await ctrl.Create(GlobalWeekly("Nightly"), CancellationToken.None);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        db.MaintenanceWindows.Should().ContainSingle(w => w.Name == "Nightly");
    }

    [Fact]
    public async Task Create_IanaTimeZone_Accepted()
    {
        await using var db = TestDbFactory.Create();
        var ctrl = Build(db);
        var req = GlobalWeekly("BerlinWin") with { TimeZoneId = "Europe/Berlin" };

        (await ctrl.Create(req, CancellationToken.None)).Result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Create_FullDayWeekly_StartEqualsEnd_Accepted()
    {
        await using var db = TestDbFactory.Create();
        var ctrl = Build(db);
        // "every Mon–Fri, all day" — start == end is a full 24h window, no longer rejected.
        var req = GlobalWeekly("AllDay") with { WeeklyStartMinuteOfDay = 0, WeeklyEndMinuteOfDay = 0 };

        (await ctrl.Create(req, CancellationToken.None)).Result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Create_InvalidMode_Returns400()
    {
        await using var db = TestDbFactory.Create();
        var ctrl = Build(db);
        var req = GlobalWeekly("Bad") with { Mode = "Nope" };

        (await ctrl.Create(req, CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_CronWithValidExpressionAndDuration_Created()
    {
        await using var db = TestDbFactory.Create();
        var ctrl = Build(db);

        var result = await ctrl.Create(GlobalCron("SatPatch"), CancellationToken.None);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        db.MaintenanceWindows.Should().ContainSingle(w => w.Name == "SatPatch");
    }

    [Fact]
    public async Task Create_CronWithInvalidExpression_Returns400()
    {
        await using var db = TestDbFactory.Create();
        var ctrl = Build(db);
        var req = GlobalCron("BadCron") with { CronExpression = "not a cron" };

        (await ctrl.Create(req, CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_CronWithoutExpression_Returns400()
    {
        await using var db = TestDbFactory.Create();
        var ctrl = Build(db);
        var req = GlobalCron("NoExpr") with { CronExpression = null };

        (await ctrl.Create(req, CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Create_CronWithMissingOrNonPositiveDuration_Returns400(int? durationMinutes)
    {
        await using var db = TestDbFactory.Create();
        var ctrl = Build(db);
        var req = GlobalCron("BadDuration") with { DurationMinutes = durationMinutes };

        (await ctrl.Create(req, CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_CronDurationOverSevenDays_Returns400()
    {
        await using var db = TestDbFactory.Create();
        var ctrl = Build(db);
        var req = GlobalCron("TooLong") with { DurationMinutes = 7 * 24 * 60 + 1 };

        (await ctrl.Create(req, CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_Cron_RoundTripsCronExpressionAndDuration()
    {
        await using var db = TestDbFactory.Create();
        var ctrl = Build(db);

        var created = (await ctrl.Create(GlobalCron("RoundTrip"), CancellationToken.None)).Result
            .Should().BeOfType<CreatedAtActionResult>().Subject;
        var id = ((MaintenanceWindowResponse)created.Value!).Id;

        var ok = (await ctrl.Get(id, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>().Subject;
        var got = (MaintenanceWindowResponse)ok.Value!;
        got.Recurrence.Should().Be("Cron");
        got.CronExpression.Should().Be("0 0 3 ? * SAT");
        got.DurationMinutes.Should().Be(90);
        // Weekly/OneTime fields must be nulled for a Cron window.
        got.WeeklyStartMinuteOfDay.Should().BeNull();
        got.WeeklyEndMinuteOfDay.Should().BeNull();
        got.OneTimeStartUtc.Should().BeNull();
    }

    [Fact]
    public async Task Create_WeeklyWithoutDays_Returns400()
    {
        await using var db = TestDbFactory.Create();
        var ctrl = Build(db);
        var req = GlobalWeekly("NoDays") with { WeeklyDaysMask = 0 };

        (await ctrl.Create(req, CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_FoldersScopeWithoutTargets_Returns400()
    {
        await using var db = TestDbFactory.Create();
        var ctrl = Build(db);
        var req = GlobalWeekly("FolderWin") with { ScopeKind = "Folders", Targets = null };

        (await ctrl.Create(req, CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Affecting_ReturnsGlobalWindowForAnyWorkflow()
    {
        await using var db = TestDbFactory.Create();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();
        var ctrl = Build(db);
        await ctrl.Create(GlobalWeekly("Everywhere"), CancellationToken.None); // refreshes evaluator inline

        var result = await ctrl.Affecting(wf.Id, CancellationToken.None);

        var list = (result.Result as OkObjectResult)!.Value as List<MaintenanceWindowAffectingDto>;
        list.Should().ContainSingle(w => w.Name == "Everywhere");
    }

    [Fact]
    public async Task Affecting_OperatorWithoutFolderRead_ReturnsMasked404()
    {
        await using var db = TestDbFactory.Create();
        var folder = new SharedWorkflowFolder
        {
            Id = Guid.NewGuid(), ParentFolderId = SharedWorkflowFolder.RootFolderId,
            Name = "Restricted", Path = "/Restricted", Depth = 1,
        };
        var wf = new Workflow
        {
            Id = Guid.NewGuid(), Name = "Hidden", DefinitionJson = "{}", FolderId = folder.Id,
        };
        db.AddRange(folder, wf);
        await db.SaveChangesAsync();
        var ctrl = Build(db);
        ctrl.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Operator"),
        ], "test"));

        var result = await ctrl.Affecting(wf.Id, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }
}
