using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// Controller tests for the global-variable folder tree. Verifies the store's typed exceptions
/// map to the right HTTP status (409 conflict / 400 validation / 404 not-found), audit entries
/// are written, and the variable → folder move endpoint on <see cref="GlobalVariablesController"/>
/// behaves.
/// </summary>
public class GlobalVariableFoldersControllerTests
{
    private static readonly Guid Root = GlobalVariableFolder.RootFolderId;

    private static GlobalVariableFolderStore FolderStore(NodePilotDbContext db) => new(db);
    private static GlobalVariableStore VarStore(NodePilotDbContext db)
        => new(db, new NodePilot.Data.Security.DpapiSecretProtector(System.Security.Cryptography.DataProtectionScope.CurrentUser));

    private static T WithUser<T>(T controller) where T : ControllerBase
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Role, "Admin"), new Claim(ClaimTypes.Name, "testadmin") }, "TestAuth"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
        return controller;
    }

    private static (GlobalVariableFoldersController controller, CapturingAuditWriter audit) NewController(NodePilotDbContext db)
    {
        var audit = new CapturingAuditWriter();
        return (WithUser(new GlobalVariableFoldersController(FolderStore(db), audit)), audit);
    }

    [Fact]
    public async Task Create_Valid_Returns201_AndAudits()
    {
        using var db = TestDbFactory.Create();
        var (controller, audit) = NewController(db);

        var result = await controller.Create(new CreateGlobalVariableFolderRequest(null, "Databases"), CancellationToken.None);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        audit.Calls.Should().ContainSingle(c => c.Action == "GLOBAL_VARIABLE_FOLDER_CREATED");
    }

    [Fact]
    public async Task Create_DuplicateSibling_Returns409()
    {
        using var db = TestDbFactory.Create();
        var (controller, _) = NewController(db);
        await controller.Create(new CreateGlobalVariableFolderRequest(null, "Dup"), CancellationToken.None);

        var result = await controller.Create(new CreateGlobalVariableFolderRequest(null, "Dup"), CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Create_BlankName_Returns400()
    {
        using var db = TestDbFactory.Create();
        var (controller, _) = NewController(db);

        var result = await controller.Create(new CreateGlobalVariableFolderRequest(null, "   "), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Rename_Root_Returns400()
    {
        using var db = TestDbFactory.Create();
        var (controller, _) = NewController(db);

        var result = await controller.Rename(Root, new UpdateGlobalVariableFolderRequest("X"), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Delete_NonEmpty_Returns409()
    {
        using var db = TestDbFactory.Create();
        var folder = await FolderStore(db).CreateAsync(Root, "HasChild", null, CancellationToken.None);
        await FolderStore(db).CreateAsync(folder.Id, "Child", null, CancellationToken.None);
        var (controller, _) = NewController(db);

        var result = await controller.Delete(folder.Id, CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Delete_Empty_Returns204_AndAudits()
    {
        using var db = TestDbFactory.Create();
        var folder = await FolderStore(db).CreateAsync(Root, "Temp", null, CancellationToken.None);
        var (controller, audit) = NewController(db);

        var result = await controller.Delete(folder.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        audit.Calls.Should().ContainSingle(c => c.Action == "GLOBAL_VARIABLE_FOLDER_DELETED");
    }

    [Fact]
    public async Task Delete_NotFound_Returns404()
    {
        using var db = TestDbFactory.Create();
        var (controller, _) = NewController(db);

        var result = await controller.Delete(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetAll_IncludesSeededRoot()
    {
        using var db = TestDbFactory.Create();
        var (controller, _) = NewController(db);

        var result = await controller.GetAll(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var list = ok.Value.Should().BeAssignableTo<List<GlobalVariableFolderResponse>>().Subject;
        list.Should().Contain(f => f.Id == Root);
    }

    // ---- Variable → folder move (GlobalVariablesController.MoveToFolder) ----

    private static GlobalVariablesController NewVarController(NodePilotDbContext db, out CapturingAuditWriter audit)
    {
        audit = new CapturingAuditWriter();
        return WithUser(new GlobalVariablesController(VarStore(db), FolderStore(db), audit));
    }

    [Fact]
    public async Task MoveVariable_ToExistingFolder_Returns204_AndAudits()
    {
        using var db = TestDbFactory.Create();
        var folder = await FolderStore(db).CreateAsync(Root, "Target", null, CancellationToken.None);
        var v = await VarStore(db).CreateAsync("MOVE_ME", "v", false, null, Root, "t", CancellationToken.None);
        var controller = NewVarController(db, out var audit);

        var result = await controller.MoveToFolder(v.Id, new MoveGlobalVariableRequest(folder.Id), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        db.GlobalVariables.Find(v.Id)!.FolderId.Should().Be(folder.Id);
        audit.Calls.Should().ContainSingle(c => c.Action == "GLOBAL_VARIABLE_MOVED");
    }

    [Fact]
    public async Task MoveVariable_ToNonexistentFolder_Returns400()
    {
        using var db = TestDbFactory.Create();
        var v = await VarStore(db).CreateAsync("MOVE_ME", "v", false, null, Root, "t", CancellationToken.None);
        var controller = NewVarController(db, out _);

        var result = await controller.MoveToFolder(v.Id, new MoveGlobalVariableRequest(Guid.NewGuid()), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task MoveVariable_NonexistentVariable_Returns404()
    {
        using var db = TestDbFactory.Create();
        var controller = NewVarController(db, out _);

        var result = await controller.MoveToFolder(Guid.NewGuid(), new MoveGlobalVariableRequest(Root), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }
}
