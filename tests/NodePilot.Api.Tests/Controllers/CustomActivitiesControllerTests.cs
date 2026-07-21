using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Core.Activities;
using NodePilot.Core.Interfaces;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public class CustomActivitiesControllerTests
{
    private static (CustomActivitiesController controller, CapturingAuditWriter audit, CustomActivityDefinitionStore store)
        NewController(NodePilotDbContext db, string role = "Admin")
    {
        var store = new CustomActivityDefinitionStore(db);
        var audit = new CapturingAuditWriter();
        var controller = new CustomActivitiesController(store, audit);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Role, role), new Claim(ClaimTypes.Name, "testuser") }, "TestAuth"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
        return (controller, audit, store);
    }

    private static CreateCustomActivityRequest CreateReq(
        string key = "disk_check", string name = "Disk Check",
        IReadOnlyList<CustomActivityInputParameter>? inputs = null,
        IReadOnlyList<CustomActivityOutputParameter>? outputs = null,
        string script = "Get-PSDrive C")
        => new(key, name, null, "extension", null, script, "auto", false, false, null, null, null, null, inputs, outputs);

    [Fact]
    public async Task Create_StartsDisabled_AndAudits()
    {
        var db = TestDbFactory.Create();
        var (c, audit, _) = NewController(db, "Operator");

        var res = await c.Create(CreateReq(), CancellationToken.None);

        var created = res.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        var saved = created.Value.Should().BeOfType<CustomActivitySaveResponse>().Subject;
        saved.Definition.IsEnabled.Should().BeFalse("created as a Draft");
        saved.Definition.Type.Should().Be("custom:disk_check");
        audit.Calls.Should().ContainSingle(x => x.Action == "CUSTOM_ACTIVITY_CREATED");
    }

    [Fact]
    public async Task Create_OutputNamedExitCode_Rejected()
    {
        var db = TestDbFactory.Create();
        var (c, _, _) = NewController(db);

        var res = await c.Create(CreateReq(outputs: [new CustomActivityOutputParameter("exitCode", "number")]), CancellationToken.None);

        res.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_InputAndOutputSameName_Rejected()
    {
        var db = TestDbFactory.Create();
        var (c, _, _) = NewController(db);

        var res = await c.Create(CreateReq(
            inputs: [new CustomActivityInputParameter("dup", "Dup", "string")],
            outputs: [new CustomActivityOutputParameter("dup", "string")]), CancellationToken.None);

        res.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Operator_CannotUpdateEnabledDefinition()
    {
        var db = TestDbFactory.Create();
        var store = new CustomActivityDefinitionStore(db);
        var def = await store.CreateAsync(new CustomActivityDefinitionInput { Key = "k", Name = "K", ScriptTemplate = "x" }, "op", CancellationToken.None);
        await store.SetEnabledAsync(def.Id, true, "admin", CancellationToken.None);

        var (c, _, _) = NewController(db, "Operator");
        var res = await c.Update(def.Id, new UpdateCustomActivityRequest(
            "K2", null, "extension", null, "y", "auto", false, false, null, null, null, null, null, null,
            def.ConcurrencyToken, null), CancellationToken.None);

        var obj = res.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Admin_CanUpdateEnabledDefinition()
    {
        var db = TestDbFactory.Create();
        var store = new CustomActivityDefinitionStore(db);
        var def = await store.CreateAsync(new CustomActivityDefinitionInput { Key = "k", Name = "K", ScriptTemplate = "x" }, "op", CancellationToken.None);
        await store.SetEnabledAsync(def.Id, true, "admin", CancellationToken.None);
        var live = await store.GetByIdAsync(def.Id, CancellationToken.None);

        var (c, _, _) = NewController(db, "Admin");
        var res = await c.Update(def.Id, new UpdateCustomActivityRequest(
            "K2", null, "extension", null, "y", "auto", false, false, null, null, null, null, null, null,
            live!.ConcurrencyToken, null), CancellationToken.None);

        res.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Update_StaleToken_Returns409()
    {
        var db = TestDbFactory.Create();
        var (c, _, store) = NewController(db, "Admin");
        var def = await store.CreateAsync(new CustomActivityDefinitionInput { Key = "k", Name = "K", ScriptTemplate = "x" }, "u", CancellationToken.None);
        var stale = def.ConcurrencyToken;
        await store.UpdateAsync(def.Id, new CustomActivityDefinitionInput { Key = "k", Name = "v2", ScriptTemplate = "x" }, stale, "u", CancellationToken.None);

        var res = await c.Update(def.Id, new UpdateCustomActivityRequest(
            "v3", null, "extension", null, "x", "auto", false, false, null, null, null, null, null, null, stale, null), CancellationToken.None);

        res.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Import_LandsDisabled_AndAudits()
    {
        var db = TestDbFactory.Create();
        var (c, audit, store) = NewController(db, "Admin");

        var envelope = new CustomActivityExportEnvelope("nodepilot-customactivity-export/v1", 1, DateTime.UtcNow,
        [
            new CustomActivityExportItem("imported_op", "Imported Op", null, "extension", null, "Write-Output 1",
                "auto", false, false, null, null, null, null, [], [])
        ]);

        var res = await c.Import(envelope, CancellationToken.None);

        var ok = res.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeAssignableTo<IReadOnlyList<CustomActivityResponse>>()
            .Which.Should().ContainSingle(d => d.Key == "imported_op" && !d.IsEnabled);
        audit.Calls.Should().ContainSingle(x => x.Action == "CUSTOM_ACTIVITY_IMPORTED");
        (await store.GetAllAsync(includeDisabled: false, CancellationToken.None)).Should().BeEmpty("imported definitions are disabled until an admin enables them");
    }
}
