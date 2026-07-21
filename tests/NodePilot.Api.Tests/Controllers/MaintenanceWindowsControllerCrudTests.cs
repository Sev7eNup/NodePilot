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
/// CRUD-read/update/delete coverage for <see cref="MaintenanceWindowsController"/> —
/// the GetAll/Get/Update/Delete paths (incl. 404 + 400 branches) not exercised by the
/// create-focused <see cref="MaintenanceWindowsControllerTests"/>.
/// </summary>
public class MaintenanceWindowsControllerCrudTests
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

    private static UpdateMaintenanceWindowRequest UpdateOf(string name) => new(
        name, "edited", false, "AllowOnly", "Global", "Weekly",
        null, null, 0b0111110, 8 * 60, 17 * 60, null, null, "UTC", null);

    private static async Task<Guid> SeedWindow(MaintenanceWindowsController ctrl, string name)
    {
        var created = (await ctrl.Create(GlobalWeekly(name), CancellationToken.None)).Result
            .Should().BeOfType<CreatedAtActionResult>().Subject;
        return ((MaintenanceWindowResponse)created.Value!).Id;
    }

    [Fact]
    public async Task GetAll_ReturnsAllWindows()
    {
        await using var db = TestDbFactory.Create();
        var ctrl = Build(db);
        await SeedWindow(ctrl, "W1");
        await SeedWindow(ctrl, "W2");

        var ok = (await ctrl.GetAll(CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>().Subject;
        var list = (List<MaintenanceWindowResponse>)ok.Value!;
        list.Should().HaveCount(2);
        list.Select(w => w.Name).Should().Contain(new[] { "W1", "W2" });
    }

    [Fact]
    public async Task Get_ExistingId_ReturnsWindow()
    {
        await using var db = TestDbFactory.Create();
        var ctrl = Build(db);
        var id = await SeedWindow(ctrl, "Findable");

        var ok = (await ctrl.Get(id, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>().Subject;
        ((MaintenanceWindowResponse)ok.Value!).Name.Should().Be("Findable");
    }

    [Fact]
    public async Task Get_UnknownId_Returns404()
    {
        await using var db = TestDbFactory.Create();
        var ctrl = Build(db);

        (await ctrl.Get(Guid.NewGuid(), CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Update_ExistingId_AppliesChanges()
    {
        await using var db = TestDbFactory.Create();
        var ctrl = Build(db);
        var id = await SeedWindow(ctrl, "Before");

        var result = await ctrl.Update(id, UpdateOf("After"), CancellationToken.None);
        result.Should().BeOfType<NoContentResult>();

        var ok = (await ctrl.Get(id, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>().Subject;
        var updated = (MaintenanceWindowResponse)ok.Value!;
        updated.Name.Should().Be("After");
        updated.IsEnabled.Should().BeFalse();
        updated.Mode.Should().Be("AllowOnly");
    }

    [Fact]
    public async Task Update_UnknownId_Returns404()
    {
        await using var db = TestDbFactory.Create();
        var ctrl = Build(db);

        (await ctrl.Update(Guid.NewGuid(), UpdateOf("Ghost"), CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Update_InvalidPayload_Returns400_WithoutTouchingStore()
    {
        await using var db = TestDbFactory.Create();
        var ctrl = Build(db);
        var id = await SeedWindow(ctrl, "Keep");

        var bad = UpdateOf("Keep") with { Mode = "Nonsense" };
        (await ctrl.Update(id, bad, CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();

        // The original row must be untouched after a rejected update.
        var ok = (await ctrl.Get(id, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>().Subject;
        ((MaintenanceWindowResponse)ok.Value!).Mode.Should().Be("Blackout");
    }

    [Fact]
    public async Task Delete_ExistingId_RemovesRow()
    {
        await using var db = TestDbFactory.Create();
        var ctrl = Build(db);
        var id = await SeedWindow(ctrl, "Doomed");

        (await ctrl.Delete(id, CancellationToken.None)).Should().BeOfType<NoContentResult>();
        (await ctrl.Get(id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Delete_UnknownId_Returns404()
    {
        await using var db = TestDbFactory.Create();
        var ctrl = Build(db);

        (await ctrl.Delete(Guid.NewGuid(), CancellationToken.None)).Should().BeOfType<NotFoundResult>();
    }
}
