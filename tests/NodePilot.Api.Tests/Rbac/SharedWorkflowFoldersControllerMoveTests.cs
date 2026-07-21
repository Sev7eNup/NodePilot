using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.Security;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Rbac;

/// <summary>
/// Exercises the rename/move/move-workflow paths of <see cref="SharedWorkflowFoldersController"/>
/// — in particular the recursive path/depth recomputation helpers over multi-level subtrees that
/// the CRUD-focused fixture does not reach. Runs through the real
/// <see cref="ResourceAuthorizationService"/> as Admin (unrestricted).
/// </summary>
public sealed class SharedWorkflowFoldersControllerMoveTests
{
    private static SharedWorkflowFoldersController NewCtrl(
        NodePilotDbContext db,
        IResourceAuthorizationService? authz = null,
        IAuditWriter? audit = null,
        Guid? userId = null)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString()),
            new Claim(ClaimTypes.Role, "Admin"),
        ], "test"));
        var ctrl = new SharedWorkflowFoldersController(
            db, audit ?? NoopAuditWriter.Instance, authz ?? new ResourceAuthorizationService(db));
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
        return ctrl;
    }

    private static Guid AddFolder(NodePilotDbContext db, Guid parentId, string name, string path, int depth)
    {
        var id = Guid.NewGuid();
        db.SharedWorkflowFolders.Add(new SharedWorkflowFolder
        {
            Id = id, ParentFolderId = parentId, Name = name, Path = path, Depth = depth,
        });
        return id;
    }

    [Fact]
    public async Task Rename_RecomputesDescendantPaths()
    {
        await using var db = TestDbFactory.Create();
        var root = SharedWorkflowFolder.RootFolderId;
        var a = AddFolder(db, root, "A", "/A", 1);
        var b = AddFolder(db, a, "B", "/A/B", 2);
        var c = AddFolder(db, b, "C", "/A/B/C", 3);
        await db.SaveChangesAsync();

        var result = await NewCtrl(db).Rename(a, new UpdateSharedFolderRequest("A2"), CancellationToken.None);
        result.Should().BeOfType<NoContentResult>();

        db.ChangeTracker.Clear();
        (await db.SharedWorkflowFolders.FindAsync(a))!.Path.Should().Be("/A2");
        (await db.SharedWorkflowFolders.FindAsync(b))!.Path.Should().Be("/A2/B");
        (await db.SharedWorkflowFolders.FindAsync(c))!.Path.Should().Be("/A2/B/C");
    }

    [Fact]
    public async Task Rename_RootFolder_Returns400()
    {
        await using var db = TestDbFactory.Create();
        (await NewCtrl(db).Rename(SharedWorkflowFolder.RootFolderId, new UpdateSharedFolderRequest("x"), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Rename_UnknownFolder_Returns404()
    {
        await using var db = TestDbFactory.Create();
        (await NewCtrl(db).Rename(Guid.NewGuid(), new UpdateSharedFolderRequest("x"), CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Rename_SiblingNameClash_Returns409()
    {
        await using var db = TestDbFactory.Create();
        var root = SharedWorkflowFolder.RootFolderId;
        var a = AddFolder(db, root, "A", "/A", 1);
        AddFolder(db, root, "B", "/B", 1);
        await db.SaveChangesAsync();

        (await NewCtrl(db).Rename(a, new UpdateSharedFolderRequest("B"), CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Move_ToNewParent_RecomputesSubtreePathsAndDepths()
    {
        await using var db = TestDbFactory.Create();
        var root = SharedWorkflowFolder.RootFolderId;
        var a = AddFolder(db, root, "A", "/A", 1);
        var b = AddFolder(db, a, "B", "/A/B", 2);
        var d = AddFolder(db, root, "D", "/D", 1);
        await db.SaveChangesAsync();

        var result = await NewCtrl(db).Move(a, new MoveSharedFolderRequest(d), CancellationToken.None);
        result.Should().BeOfType<NoContentResult>();

        db.ChangeTracker.Clear();
        var movedA = (await db.SharedWorkflowFolders.FindAsync(a))!;
        movedA.ParentFolderId.Should().Be(d);
        movedA.Path.Should().Be("/D/A");
        movedA.Depth.Should().Be(2);
        var movedB = (await db.SharedWorkflowFolders.FindAsync(b))!;
        movedB.Path.Should().Be("/D/A/B");
        movedB.Depth.Should().Be(3);
    }

    [Fact]
    public async Task Move_WhenDescendantContainsForeignLockedWorkflow_Returns423AndKeepsTree()
    {
        await using var db = TestDbFactory.Create();
        var root = SharedWorkflowFolder.RootFolderId;
        var a = AddFolder(db, root, "A", "/A", 1);
        var b = AddFolder(db, a, "B", "/A/B", 2);
        var d = AddFolder(db, root, "D", "/D", 1);
        db.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "locked",
            DefinitionJson = "{}",
            FolderId = b,
            CheckedOutByUserId = Guid.NewGuid(),
            CheckedOutAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var result = await NewCtrl(db).Move(a, new MoveSharedFolderRequest(d), CancellationToken.None);

        var locked = result.Should().BeOfType<ObjectResult>().Subject;
        locked.StatusCode.Should().Be(StatusCodes.Status423Locked);
        db.ChangeTracker.Clear();
        var unchanged = (await db.SharedWorkflowFolders.FindAsync(a))!;
        unchanged.ParentFolderId.Should().Be(root);
        unchanged.Path.Should().Be("/A");
    }

    [Fact]
    public async Task Move_ToRoot_WhenNewParentNull_Succeeds()
    {
        await using var db = TestDbFactory.Create();
        var root = SharedWorkflowFolder.RootFolderId;
        var a = AddFolder(db, root, "A", "/A", 1);
        var b = AddFolder(db, a, "B", "/A/B", 2);
        await db.SaveChangesAsync();

        // Null new-parent → move under Root.
        var result = await NewCtrl(db).Move(b, new MoveSharedFolderRequest(null), CancellationToken.None);
        result.Should().BeOfType<NoContentResult>();

        db.ChangeTracker.Clear();
        var movedB = (await db.SharedWorkflowFolders.FindAsync(b))!;
        movedB.ParentFolderId.Should().Be(root);
        movedB.Path.Should().Be("/B");
        movedB.Depth.Should().Be(1);
    }

    [Fact]
    public async Task Move_WouldExceedDepthLimit_Returns400()
    {
        await using var db = TestDbFactory.Create();
        var root = SharedWorkflowFolder.RootFolderId;
        // Deep destination chain: X1..X4 (depth 4).
        var x1 = AddFolder(db, root, "X1", "/X1", 1);
        var x2 = AddFolder(db, x1, "X2", "/X1/X2", 2);
        var x3 = AddFolder(db, x2, "X3", "/X1/X2/X3", 3);
        var x4 = AddFolder(db, x3, "X4", "/X1/X2/X3/X4", 4);
        // Source subtree A->B->C (descendant max-depth 2).
        var a = AddFolder(db, root, "A", "/A", 1);
        var b = AddFolder(db, a, "B", "/A/B", 2);
        AddFolder(db, b, "C", "/A/B/C", 3);
        await db.SaveChangesAsync();

        // 4 (X4) + 1 + 2 (A's subtree) = 7 > MaxDepth(5) → rejected.
        (await NewCtrl(db).Move(a, new MoveSharedFolderRequest(x4), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Move_SiblingNameClashInDestination_Returns409()
    {
        await using var db = TestDbFactory.Create();
        var root = SharedWorkflowFolder.RootFolderId;
        var a = AddFolder(db, root, "A", "/A", 1);
        var d = AddFolder(db, root, "D", "/D", 1);
        AddFolder(db, d, "A", "/D/A", 2); // destination already has an "A"
        await db.SaveChangesAsync();

        (await NewCtrl(db).Move(a, new MoveSharedFolderRequest(d), CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Move_RootFolder_Returns400()
    {
        await using var db = TestDbFactory.Create();
        (await NewCtrl(db).Move(SharedWorkflowFolder.RootFolderId, new MoveSharedFolderRequest(null), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task MoveWorkflow_ToNewFolder_UpdatesFolderId()
    {
        await using var db = TestDbFactory.Create();
        var root = SharedWorkflowFolder.RootFolderId;
        var a = AddFolder(db, root, "A", "/A", 1);
        var d = AddFolder(db, root, "D", "/D", 1);
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "wf", DefinitionJson = "{}", FolderId = a };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var result = await NewCtrl(db).MoveWorkflow(wf.Id, new MoveWorkflowToFolderRequest(d), CancellationToken.None);
        result.Should().BeOfType<NoContentResult>();

        db.ChangeTracker.Clear();
        (await db.Workflows.FindAsync(wf.Id))!.FolderId.Should().Be(d);
    }

    [Fact]
    public async Task MoveWorkflow_WhenLockedByAnotherUser_Returns423AndKeepsFolder()
    {
        await using var db = TestDbFactory.Create();
        var root = SharedWorkflowFolder.RootFolderId;
        var a = AddFolder(db, root, "A", "/A", 1);
        var d = AddFolder(db, root, "D", "/D", 1);
        var wf = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "wf",
            DefinitionJson = "{}",
            FolderId = a,
            CheckedOutByUserId = Guid.NewGuid(),
            CheckedOutAt = DateTime.UtcNow,
        };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var result = await NewCtrl(db).MoveWorkflow(
            wf.Id, new MoveWorkflowToFolderRequest(d), CancellationToken.None);

        var locked = result.Should().BeOfType<ObjectResult>().Subject;
        locked.StatusCode.Should().Be(StatusCodes.Status423Locked);
        db.ChangeTracker.Clear();
        (await db.Workflows.FindAsync(wf.Id))!.FolderId.Should().Be(a);
    }

    [Fact]
    public async Task MoveWorkflow_WhenFolderChangesAfterAuthorization_Returns409AndDoesNotOverwriteConcurrentMove()
    {
        await using var db = TestDbFactory.Create();
        var root = SharedWorkflowFolder.RootFolderId;
        var a = AddFolder(db, root, "A", "/A", 1);
        var d = AddFolder(db, root, "D", "/D", 1);
        var concurrentTarget = AddFolder(db, root, "Concurrent", "/Concurrent", 1);
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "wf", DefinitionJson = "{}", FolderId = a };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var moved = false;
        var authz = new CallbackAuthorizationService(
            onFolderCheck: async (folderId, op, ct) =>
            {
                if (!moved && folderId == d && op == ResourceOp.Edit)
                {
                    moved = true;
                    await db.Workflows.Where(w => w.Id == wf.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(w => w.FolderId, concurrentTarget), ct);
                }
            });
        var audit = new CapturingAuditWriter();

        var result = await NewCtrl(db, authz, audit).MoveWorkflow(
            wf.Id, new MoveWorkflowToFolderRequest(d), CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        db.ChangeTracker.Clear();
        (await db.Workflows.FindAsync(wf.Id))!.FolderId.Should().Be(concurrentTarget);
        audit.Calls.Should().NotContain(c => c.Action == "WORKFLOW_MOVED");
    }

    [Fact]
    public async Task MoveWorkflow_UnknownWorkflow_Returns404()
    {
        await using var db = TestDbFactory.Create();
        (await NewCtrl(db).MoveWorkflow(Guid.NewGuid(), new MoveWorkflowToFolderRequest(SharedWorkflowFolder.RootFolderId), CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task MoveWorkflow_UnknownDestination_Returns400()
    {
        await using var db = TestDbFactory.Create();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "wf", DefinitionJson = "{}" };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        (await NewCtrl(db).MoveWorkflow(wf.Id, new MoveWorkflowToFolderRequest(Guid.NewGuid()), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    private sealed class CallbackAuthorizationService(
        Func<Guid, ResourceOp, CancellationToken, Task>? onWorkflowCheck = null,
        Func<Guid, ResourceOp, CancellationToken, Task>? onFolderCheck = null)
        : IResourceAuthorizationService
    {
        public async Task<bool> CanAccessWorkflowAsync(
            ClaimsPrincipal user, Guid folderId, ResourceOp op, CancellationToken ct = default)
        {
            if (onWorkflowCheck is not null) await onWorkflowCheck(folderId, op, ct);
            return true;
        }

        public async Task<bool> CanAccessFolderAsync(
            ClaimsPrincipal user, Guid folderId, ResourceOp op, CancellationToken ct = default)
        {
            if (onFolderCheck is not null) await onFolderCheck(folderId, op, ct);
            return true;
        }

        public Task<AccessibleFolderSet> GetAccessibleFolderIdsAsync(
            ClaimsPrincipal user, CancellationToken ct = default)
            => Task.FromResult(AccessibleFolderSet.Unrestricted);

        public Task<ResourceCapabilities> GetWorkflowCapabilitiesAsync(
            ClaimsPrincipal user, Guid folderId, CancellationToken ct = default)
            => Task.FromResult(ResourceCapabilities.All);

        public Task<ResourceCapabilities> GetFolderCapabilitiesAsync(
            ClaimsPrincipal user, Guid folderId, CancellationToken ct = default)
            => Task.FromResult(ResourceCapabilities.All);

        public Task<SharedFolderRole?> GetEffectiveFolderRoleAsync(
            ClaimsPrincipal user, Guid folderId, CancellationToken ct = default)
            => Task.FromResult<SharedFolderRole?>(SharedFolderRole.FolderAdmin);

        public void InvalidateAll() { }
    }
}
