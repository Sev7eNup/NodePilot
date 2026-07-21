using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// Exhaustive validation-branch coverage for <see cref="MaintenanceWindowsController"/>'s
/// draft builder (scope/recurrence/timezone/one-time/weekly/target checks) plus the
/// affecting-lookup 404 path — the branches not reached by the create-happy-path or CRUD suites.
/// </summary>
public class MaintenanceWindowsControllerValidationTests
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

    private static CreateMaintenanceWindowRequest Weekly(string name) => new(
        name, null, true, "Blackout", "Global", "Weekly",
        null, null, 0b0111110, 22 * 60, 23 * 60, null, null, "UTC", null);

    private static async Task<IActionResult> CreateResult(MaintenanceWindowsController ctrl, CreateMaintenanceWindowRequest req)
        => (await ctrl.Create(req, CancellationToken.None)).Result!;

    [Fact]
    public async Task Create_EmptyName_Returns400()
    {
        await using var db = TestDbFactory.Create();
        (await CreateResult(Build(db), Weekly("x") with { Name = "  " })).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_NameTooLong_Returns400()
    {
        await using var db = TestDbFactory.Create();
        (await CreateResult(Build(db), Weekly("x") with { Name = new string('n', 101) }))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_DescriptionTooLong_Returns400()
    {
        await using var db = TestDbFactory.Create();
        (await CreateResult(Build(db), Weekly("x") with { Description = new string('d', 501) }))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_InvalidScopeKind_Returns400()
    {
        await using var db = TestDbFactory.Create();
        (await CreateResult(Build(db), Weekly("x") with { ScopeKind = "Nope" })).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_InvalidRecurrence_Returns400()
    {
        await using var db = TestDbFactory.Create();
        (await CreateResult(Build(db), Weekly("x") with { Recurrence = "Fortnightly" }))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_UnknownTimeZone_Returns400()
    {
        await using var db = TestDbFactory.Create();
        (await CreateResult(Build(db), Weekly("x") with { TimeZoneId = "Mars/Olympus" }))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_OneTime_MissingBounds_Returns400()
    {
        await using var db = TestDbFactory.Create();
        var req = Weekly("x") with { Recurrence = "OneTime", OneTimeStartUtc = null, OneTimeEndUtc = null };
        (await CreateResult(Build(db), req)).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_OneTime_EndBeforeStart_Returns400()
    {
        await using var db = TestDbFactory.Create();
        var start = new DateTime(2026, 7, 8, 10, 0, 0, DateTimeKind.Utc);
        var req = Weekly("x") with { Recurrence = "OneTime", OneTimeStartUtc = start, OneTimeEndUtc = start.AddHours(-1) };
        (await CreateResult(Build(db), req)).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_OneTime_Valid_Created()
    {
        await using var db = TestDbFactory.Create();
        var start = new DateTime(2026, 7, 8, 10, 0, 0, DateTimeKind.Utc);
        var req = Weekly("Maint") with { Recurrence = "OneTime", OneTimeStartUtc = start, OneTimeEndUtc = start.AddHours(2) };
        (await CreateResult(Build(db), req)).Should().BeOfType<CreatedAtActionResult>();
    }

    [Theory]
    [InlineData(-1, 60)]
    [InlineData(60, 1440)]
    public async Task Create_Weekly_MinuteOutOfRange_Returns400(int start, int end)
    {
        await using var db = TestDbFactory.Create();
        var req = Weekly("x") with { WeeklyStartMinuteOfDay = start, WeeklyEndMinuteOfDay = end };
        (await CreateResult(Build(db), req)).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_FoldersScope_InvalidTargetKind_Returns400()
    {
        await using var db = TestDbFactory.Create();
        var req = Weekly("x") with
        {
            ScopeKind = "Folders",
            Targets = [new MaintenanceWindowTargetDto("Nonsense", Guid.NewGuid())],
        };
        (await CreateResult(Build(db), req)).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_FoldersScope_WrongTargetKind_Returns400()
    {
        await using var db = TestDbFactory.Create();
        // Folders scope requires Folder targets — a Workflow target is rejected.
        var req = Weekly("x") with
        {
            ScopeKind = "Folders",
            Targets = [new MaintenanceWindowTargetDto("Workflow", Guid.NewGuid())],
        };
        (await CreateResult(Build(db), req)).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_FoldersScope_EmptyTargetId_Returns400()
    {
        await using var db = TestDbFactory.Create();
        var req = Weekly("x") with
        {
            ScopeKind = "Folders",
            Targets = [new MaintenanceWindowTargetDto("Folder", Guid.Empty)],
        };
        (await CreateResult(Build(db), req)).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_FoldersScope_ValidFolderTarget_Created()
    {
        await using var db = TestDbFactory.Create();
        var req = Weekly("FolderWin") with
        {
            ScopeKind = "Folders",
            Targets = [new MaintenanceWindowTargetDto("Folder", Guid.NewGuid())],
        };
        var created = (await CreateResult(Build(db), req)).Should().BeOfType<CreatedAtActionResult>().Subject;
        var resp = (MaintenanceWindowResponse)created.Value!;
        resp.ScopeKind.Should().Be("Folders");
        resp.Targets.Should().ContainSingle().Which.TargetKind.Should().Be("Folder");
    }

    [Fact]
    public async Task Affecting_UnknownWorkflow_Returns404()
    {
        await using var db = TestDbFactory.Create();
        (await Build(db).Affecting(Guid.NewGuid(), CancellationToken.None)).Result
            .Should().BeOfType<NotFoundResult>();
    }
}
