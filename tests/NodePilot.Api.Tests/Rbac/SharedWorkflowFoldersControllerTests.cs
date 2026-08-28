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
using NodePilot.Core.Models;
using NodePilot.TestCommons;
using NodePilot.Api.Tests.TestSupport;
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
        => NewCtrl(userId, role, NoopAuditWriter.Instance);

    private SharedWorkflowFoldersController NewCtrl(Guid userId, string role, IAuditWriter audit)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, role),
        ], "test"));
        var ctrl = new SharedWorkflowFoldersController(
            _db, audit, new ResourceAuthorizationService(_db),
            new RecordingHubContext(), new RecordingFolderProjection());
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
        return ctrl;
    }

    /// <summary>/Finance/Reports with one workflow in each level. Returns (reportsId, financeWfId,
    /// reportsWfId).</summary>
    private async Task<(Guid ReportsId, Guid FinanceWorkflowId, Guid ReportsWorkflowId)> SeedSubtreeAsync()
    {
        var reportsId = Guid.NewGuid();
        _db.SharedWorkflowFolders.Add(new SharedWorkflowFolder
        {
            Id = reportsId, ParentFolderId = _financeId, Name = "Reports", Path = "/Finance/Reports", Depth = 2
        });
        var financeWf = Guid.NewGuid();
        var reportsWf = Guid.NewGuid();
        _db.Workflows.Add(new Workflow { Id = financeWf, Name = "top", DefinitionJson = "{}", FolderId = _financeId, Version = 1 });
        _db.Workflows.Add(new Workflow { Id = reportsWf, Name = "nested", DefinitionJson = "{}", FolderId = reportsId, Version = 1 });
        await _db.SaveChangesAsync();
        return (reportsId, financeWf, reportsWf);
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
    public async Task Create_AsStranger_OnRoot_MasksAs404()
    {
        // Create runs through the shared RBAC gate (ResourceAuthorizationGateExtensions), which
        // masks folders the caller cannot even read as 404, matching the 403/404 differential
        // rule every other folder endpoint follows. This avoids leaking that the parent exists.
        var ctrl = NewCtrl(_strangerId, "Operator");
        var result = await ctrl.Create(
            new CreateSharedFolderRequest(SharedWorkflowFolder.RootFolderId, "Stuff"),
            CancellationToken.None);
        result.Result.Should().BeOfType<NotFoundResult>();
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
        // Add a workflow to Finance, then try to delete Finance; it should fail.
        _db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "wf", DefinitionJson = "{}", FolderId = _financeId, Version = 1 });
        await _db.SaveChangesAsync();
        var ctrl = NewCtrl(_adminId, "Admin");
        var result = await ctrl.Delete(_financeId, CancellationToken.None);
        result.Should().BeOfType<ConflictObjectResult>();
    }

    // ---- recursive delete ------------------------------------------------
    // `recursive=false` keeps the 409 above; everything below is the opt-in subtree delete.

    [Fact]
    public async Task DeleteRecursive_RemovesSubfoldersAndWorkflows()
    {
        var (reportsId, _, _) = await SeedSubtreeAsync();
        var ctrl = NewCtrl(_adminId, "Admin");

        var result = await ctrl.Delete(_financeId, CancellationToken.None, recursive: true);

        var body = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<RecursiveFolderDeleteResponse>().Subject;
        body.DeletedFolders.Should().Be(2);
        body.DeletedWorkflows.Should().Be(2);
        _db.SharedWorkflowFolders.Any(f => f.Id == _financeId || f.Id == reportsId).Should().BeFalse();
        _db.Workflows.Any().Should().BeFalse();
    }

    [Fact]
    public async Task DeleteRecursive_RootFolder_Returns400()
    {
        var ctrl = NewCtrl(_adminId, "Admin");
        var result = await ctrl.Delete(SharedWorkflowFolder.RootFolderId, CancellationToken.None, recursive: true);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task DeleteRecursive_ForeignLockInSubtree_Returns423_AndDeletesNothing()
    {
        // The lock guard rides inside the delete rather than in a check before it, so this also
        // covers the TOCTOU case: a lock taken after the count still short-circuits the run.
        var (reportsId, financeWf, reportsWf) = await SeedSubtreeAsync();
        var nested = await _db.Workflows.FirstAsync(w => w.Id == reportsWf);
        nested.CheckedOutByUserId = _strangerId;
        nested.CheckedOutAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var ctrl = NewCtrl(_adminId, "Admin");
        var result = await ctrl.Delete(_financeId, CancellationToken.None, recursive: true);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status423Locked);
        // Full rollback: the unlocked sibling must survive too, not just the locked one.
        _db.Workflows.Any(w => w.Id == financeWf).Should().BeTrue();
        _db.Workflows.Any(w => w.Id == reportsWf).Should().BeTrue();
        _db.SharedWorkflowFolders.Any(f => f.Id == _financeId || f.Id == reportsId).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteRecursive_OwnLockInSubtree_Succeeds()
    {
        // Deleting your own checked-out workflow is allowed — same rule the single delete uses.
        var (_, financeWf, _) = await SeedSubtreeAsync();
        var mine = await _db.Workflows.FirstAsync(w => w.Id == financeWf);
        mine.CheckedOutByUserId = _adminId;
        mine.CheckedOutAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var ctrl = NewCtrl(_adminId, "Admin");
        var result = await ctrl.Delete(_financeId, CancellationToken.None, recursive: true);

        result.Should().BeOfType<OkObjectResult>();
        _db.Workflows.Any().Should().BeFalse();
    }

    [Fact]
    public async Task DeleteRecursive_InheritedEditOnDescendant_IsSufficient()
    {
        // Pins the assumption the recursive delete rests on: grants resolve along the ancestry
        // chain, so Edit on /Finance covers /Finance/Reports even though Reports carries no grant
        // of its own. If resolution ever stops inheriting downwards, this fails instead of
        // silently deleting something the caller may no longer be entitled to.
        var (reportsId, _, _) = await SeedSubtreeAsync();
        _db.SharedFolderPermissions.Any(p => p.FolderId == reportsId).Should().BeFalse();

        var ctrl = NewCtrl(_financeEditorId, "Operator");
        var result = await ctrl.Delete(_financeId, CancellationToken.None, recursive: true);

        result.Should().BeOfType<OkObjectResult>();
        _db.SharedWorkflowFolders.Any(f => f.Id == reportsId).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteRecursive_AsStranger_Returns404()
    {
        await SeedSubtreeAsync();
        var ctrl = NewCtrl(_strangerId, "Operator");
        var result = await ctrl.Delete(_financeId, CancellationToken.None, recursive: true);
        result.Should().BeOfType<NotFoundResult>();
    }

    /// <summary>
    /// Reproduces the interleaving the guard exists for: a workflow lands in the subtree after the
    /// controller has taken its snapshot. The interceptor writes on the same connection and inside
    /// the controller's own transaction, which puts the database in exactly the state a concurrent
    /// writer would have produced by the time the delete runs.
    /// </summary>
    private sealed class InsertWorkflowOnFirstRead(Microsoft.Data.Sqlite.SqliteConnection conn, Guid folderId)
        : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private bool _fired;
        public bool Fired => _fired;

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>>
            NonQueryExecutingAsync(
                System.Data.Common.DbCommand command,
                Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
                Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
                CancellationToken cancellationToken = default)
        {
            // Hooked on the DELETE statement, not the snapshot SELECT: an interceptor fires before
            // the command it wraps, so inserting at the SELECT would put the row into the
            // snapshot, the opposite of what this test needs. Right before the delete is exactly
            // the window a concurrent writer would use.
            // Written through EF on a second context so the column set and the Guid mapping come
            // from the model rather than a hand-written INSERT that drifts with the schema.
            if (!_fired
                && command.Transaction is not null
                && command.CommandText.Contains("DELETE FROM \"Workflows\"", StringComparison.Ordinal))
            {
                _fired = true;
                var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Data.NodePilotDbContext>()
                    .UseSqlite(conn).Options;
                await using var writer = new Data.NodePilotDbContext(options);
                await writer.Database.UseTransactionAsync(command.Transaction, cancellationToken);
                writer.Workflows.Add(new Workflow
                {
                    Id = Guid.NewGuid(), Name = "latecomer", DefinitionJson = "{}",
                    FolderId = folderId, Version = 1,
                });
                await writer.SaveChangesAsync(cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task DeleteRecursive_WorkflowAppearsBetweenSnapshotAndDelete_Returns409_AndKeepsEverything()
    {
        // Without the id-keyed delete this row would be swept up by a `FolderId IN (…)` delete:
        // gone, with no audit row, and the reported count short by one.
        var (reportsId, financeWf, reportsWf) = await SeedSubtreeAsync();

        var interceptor = new InsertWorkflowOnFirstRead(_conn, reportsId);
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Data.NodePilotDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new Data.NodePilotDbContext(options);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, _adminId.ToString()),
            new Claim(ClaimTypes.Role, "Admin"),
        ], "test"));
        var ctrl = new SharedWorkflowFoldersController(
            db, NoopAuditWriter.Instance, new ResourceAuthorizationService(db),
            new RecordingHubContext(), new RecordingFolderProjection());
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };

        var result = await ctrl.Delete(_financeId, CancellationToken.None, recursive: true);

        interceptor.Fired.Should().BeTrue("the interceptor must fire inside the transaction, otherwise the test proves nothing");
        result.Should().BeOfType<ConflictObjectResult>("a row that appeared mid-run is a race, not a lock");
        _db.ChangeTracker.Clear();
        // The interceptor writes inside the controller's own transaction, so the latecomer
        // disappears with the rollback and can't be asserted on afterwards. What matters is that
        // the run is refused as a race rather than silently sweeping up an unsnapshotted row.
        _db.Workflows.Count().Should().Be(2, "the two seeded workflows must survive the rollback");
        _db.Workflows.Any(w => w.Id == financeWf).Should().BeTrue();
        _db.Workflows.Any(w => w.Id == reportsWf).Should().BeTrue();
        _db.SharedWorkflowFolders.Any(f => f.Id == _financeId || f.Id == reportsId).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteRecursive_WritesOneAuditRowPerObject()
    {
        // A single summary row would make "who deleted workflow X" unanswerable.
        var (reportsId, financeWf, reportsWf) = await SeedSubtreeAsync();
        var audit = new CapturingAuditWriter();
        var ctrl = NewCtrl(_adminId, "Admin", audit);

        await ctrl.Delete(_financeId, CancellationToken.None, recursive: true);

        audit.Calls.Where(c => c.Action == AuditActions.WorkflowDeleted)
            .Select(c => c.ResourceId).Should().BeEquivalentTo([financeWf, reportsWf]);
        audit.Calls.Where(c => c.Action == AuditActions.FolderDeleted)
            .Select(c => c.ResourceId).Should().BeEquivalentTo([_financeId, reportsId]);
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
        // Try to move /Finance INTO /Finance/Reports â€" cycle.
        var result = await ctrl.Move(_financeId, new MoveSharedFolderRequest(reportsId), CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }
}
