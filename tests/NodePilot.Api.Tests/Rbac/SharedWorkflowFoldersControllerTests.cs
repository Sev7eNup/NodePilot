using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.Security;
using NodePilot.Api.Tests.Controllers;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Rbac;

/// <summary>
/// CRUD + move semantics for the shared-folder API. Uses the real
/// <see cref="ResourceAuthorizationService"/> so permission resolution runs through the
/// production code path; folder-tree shape is the same as in
/// <see cref="WorkflowsControllerRbacTests"/> for consistency.
/// </summary>
public sealed class SharedWorkflowFoldersControllerTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _conn;
    private readonly Data.NodePilotDbContext _db;
    private readonly Guid _adminId = Guid.NewGuid();
    private readonly Guid _financeEditorId = Guid.NewGuid();
    private readonly Guid _strangerId = Guid.NewGuid();
    private readonly Guid _financeId = Guid.NewGuid();

    public SharedWorkflowFoldersControllerTests()
    {
        var (conn, db) = TestDbFactory.CreateWithConnection();
        _conn = conn;
        _db = db;

        _db.SharedWorkflowFolders.Add(new SharedWorkflowFolder
        {
            Id = _financeId, ParentFolderId = SharedWorkflowFolder.RootFolderId,
            Name = "Finance", Path = "/Finance", Depth = 1
        });
        _db.SharedFolderPermissions.Add(new SharedFolderPermission
        {
            Id = Guid.NewGuid(), FolderId = _financeId,
            PrincipalType = FolderPrincipalType.User, PrincipalKey = _financeEditorId.ToString("D"),
            Role = SharedFolderRole.FolderEditor,
        });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private SharedWorkflowFoldersController NewCtrl(Guid userId, string role)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, role),
        ], "test"));
        var ctrl = new SharedWorkflowFoldersController(_db, NoopAuditWriter.Instance, new ResourceAuthorizationService(_db));
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
        return ctrl;
    }

    [Fact]
    public async Task GetAll_AsAdmin_ReturnsAllFolders()
    {
        var ctrl = NewCtrl(_adminId, "Admin");
        var result = await ctrl.GetAll(CancellationToken.None);
        var ok = result.Result as OkObjectResult;
        var list = ok!.Value as List<SharedFolderResponse>;
        list.Should().HaveCountGreaterThanOrEqualTo(2, "Root + Finance + any seed folders");
        list!.Should().Contain(f => f.Id == _financeId);
        list.Should().Contain(f => f.Id == SharedWorkflowFolder.RootFolderId);
    }

    [Fact]
    public async Task GetAll_AsFinanceEditor_OnlySeesFinanceTree()
    {
        var ctrl = NewCtrl(_financeEditorId, "Operator");
        var result = await ctrl.GetAll(CancellationToken.None);
        var list = (result.Result as OkObjectResult)!.Value as List<SharedFolderResponse>;
        list!.Should().Contain(f => f.Id == _financeId);
        list.Should().NotContain(f => f.Id == SharedWorkflowFolder.RootFolderId,
            "Finance editor has no Read on Root â€” Root must not appear");
    }

    [Fact]
    public async Task Create_AsAdmin_OnRoot_Succeeds_AndPathIsComputed()
    {
        var ctrl = NewCtrl(_adminId, "Admin");
        var result = await ctrl.Create(
            new CreateSharedFolderRequest(SharedWorkflowFolder.RootFolderId, "Marketing"),
            CancellationToken.None);
        var created = result.Result as CreatedAtActionResult;
        created.Should().NotBeNull();
        var folder = created!.Value as SharedFolderResponse;
        folder!.Path.Should().Be("/Marketing");
        folder.Depth.Should().Be(1);
    }

    [Fact]
    public async Task Create_DuplicateSiblingName_Returns409()
    {
        var ctrl = NewCtrl(_adminId, "Admin");
        var result = await ctrl.Create(
            new CreateSharedFolderRequest(SharedWorkflowFolder.RootFolderId, "Finance"),  // already exists
            CancellationToken.None);
        result.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Create_AsStranger_OnRoot_Returns403()
    {
        var ctrl = NewCtrl(_strangerId, "Operator");
        var result = await ctrl.Create(
            new CreateSharedFolderRequest(SharedWorkflowFolder.RootFolderId, "Stuff"),
            CancellationToken.None);
        var obj = result.Result as ObjectResult;
        obj.Should().NotBeNull();
        obj!.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task Delete_RootFolder_Returns400()
    {
        var ctrl = NewCtrl(_adminId, "Admin");
        var result = await ctrl.Delete(SharedWorkflowFolder.RootFolderId, CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Delete_NonEmptyFolder_Returns409()
    {
        // Add a workflow into Finance, then try to delete Finance â€” should fail.
        _db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "wf", DefinitionJson = "{}", FolderId = _financeId, Version = 1 });
        await _db.SaveChangesAsync();
        var ctrl = NewCtrl(_adminId, "Admin");
        var result = await ctrl.Delete(_financeId, CancellationToken.None);
        result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Move_IntoOwnDescendant_Returns400()
    {
        // Create /Finance/Reports.
        var reportsId = Guid.NewGuid();
        _db.SharedWorkflowFolders.Add(new SharedWorkflowFolder
        {
            Id = reportsId, ParentFolderId = _financeId, Name = "Reports", Path = "/Finance/Reports", Depth = 2
        });
        await _db.SaveChangesAsync();

        var ctrl = NewCtrl(_adminId, "Admin");
        // Try to move /Finance INTO /Finance/Reports â€” cycle.
        var result = await ctrl.Move(_financeId, new MoveSharedFolderRequest(reportsId), CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }
}
