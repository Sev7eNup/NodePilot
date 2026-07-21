using System.Security.Claims;
using FluentAssertions;
using NodePilot.Api.Security;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Security;

/// <summary>
/// Unit-level coverage for the four-corner permission decisions that govern every
/// workflow + folder API call. Built on a real DbContext + real folder hierarchy so
/// the inheritance walk runs the production code path; mocking the service is a known
/// way to ship security holes that pass tests but fail in production.
/// </summary>
public sealed class ResourceAuthorizationServiceTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _conn;
    private readonly NodePilotDbContext _db;

    private readonly Guid _adminId = Guid.NewGuid();
    private readonly Guid _editorId = Guid.NewGuid();
    private readonly Guid _viewerId = Guid.NewGuid();
    private readonly Guid _strangerId = Guid.NewGuid();

    private readonly Guid _financeId = Guid.NewGuid();
    private readonly Guid _financeReportsId = Guid.NewGuid();
    private readonly Guid _salesId = Guid.NewGuid();

    public ResourceAuthorizationServiceTests()
    {
        var (conn, db) = TestDbFactory.CreateWithConnection();
        _conn = conn;
        _db = db;

        // Folder tree:
        //   Root
        //   â”œâ”€â”€ Finance
        //   â”‚    â””â”€â”€ Reports
        //   â””â”€â”€ Sales
        var rootId = SharedWorkflowFolder.RootFolderId;
        _db.SharedWorkflowFolders.AddRange(
            new SharedWorkflowFolder { Id = _financeId, ParentFolderId = rootId, Name = "Finance", Path = "/Finance", Depth = 1 },
            new SharedWorkflowFolder { Id = _financeReportsId, ParentFolderId = _financeId, Name = "Reports", Path = "/Finance/Reports", Depth = 2 },
            new SharedWorkflowFolder { Id = _salesId, ParentFolderId = rootId, Name = "Sales", Path = "/Sales", Depth = 1 });

        // Permissions:
        //   editor: FolderEditor on /Finance (inherits to /Finance/Reports)
        //   viewer: FolderViewer on /Sales only
        //   stranger: no grants anywhere
        _db.SharedFolderPermissions.AddRange(
            new SharedFolderPermission { Id = Guid.NewGuid(), FolderId = _financeId, PrincipalType = FolderPrincipalType.User, PrincipalKey = _editorId.ToString("D"), Role = SharedFolderRole.FolderEditor },
            new SharedFolderPermission { Id = Guid.NewGuid(), FolderId = _salesId, PrincipalType = FolderPrincipalType.User, PrincipalKey = _viewerId.ToString("D"), Role = SharedFolderRole.FolderViewer });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private ResourceAuthorizationService NewService() => new(_db);

    private static ClaimsPrincipal MakePrincipal(Guid userId, string globalRole)
    {
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, globalRole),
        ], "test");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public async Task GlobalAdmin_BypassesFolderPermissions()
    {
        var svc = NewService();
        var admin = MakePrincipal(_adminId, "Admin");

        (await svc.CanAccessWorkflowAsync(admin, _financeReportsId, ResourceOp.Admin)).Should().BeTrue();
        (await svc.GetAccessibleFolderIdsAsync(admin)).IsUnrestricted.Should().BeTrue();
        (await svc.GetWorkflowCapabilitiesAsync(admin, _salesId)).Should().Be(ResourceCapabilities.All);
    }

    [Fact]
    public async Task Editor_OnFinance_CanReadRunEdit_Both_Finance_And_Subfolder()
    {
        var svc = NewService();
        var editor = MakePrincipal(_editorId, "Operator");

        // Direct grant.
        (await svc.CanAccessWorkflowAsync(editor, _financeId, ResourceOp.Read)).Should().BeTrue();
        (await svc.CanAccessWorkflowAsync(editor, _financeId, ResourceOp.Run)).Should().BeTrue();
        (await svc.CanAccessWorkflowAsync(editor, _financeId, ResourceOp.Edit)).Should().BeTrue();
        (await svc.CanAccessWorkflowAsync(editor, _financeId, ResourceOp.Admin)).Should().BeFalse(
            "FolderEditor does not include FolderAdmin (permission management)");

        // Inherited via /Finance â†’ /Finance/Reports.
        (await svc.CanAccessWorkflowAsync(editor, _financeReportsId, ResourceOp.Edit)).Should().BeTrue(
            "permissions inherit down the folder tree");
    }

    [Fact]
    public async Task Editor_HasNoAccess_ToSiblingTree()
    {
        var svc = NewService();
        var editor = MakePrincipal(_editorId, "Operator");

        // /Sales is a sibling of /Finance â€” no grant chain to it.
        (await svc.CanAccessWorkflowAsync(editor, _salesId, ResourceOp.Read)).Should().BeFalse();
    }

    [Fact]
    public async Task Viewer_OnSales_CanReadOnly()
    {
        var svc = NewService();
        var viewer = MakePrincipal(_viewerId, "Viewer");

        (await svc.CanAccessWorkflowAsync(viewer, _salesId, ResourceOp.Read)).Should().BeTrue();
        (await svc.CanAccessWorkflowAsync(viewer, _salesId, ResourceOp.Run)).Should().BeFalse(
            "FolderViewer does not include run rights");
        (await svc.CanAccessWorkflowAsync(viewer, _salesId, ResourceOp.Edit)).Should().BeFalse();
    }

    [Fact]
    public async Task Stranger_HasNoAccessAnywhere()
    {
        var svc = NewService();
        var stranger = MakePrincipal(_strangerId, "Operator");

        (await svc.CanAccessWorkflowAsync(stranger, _financeId, ResourceOp.Read)).Should().BeFalse();
        (await svc.CanAccessWorkflowAsync(stranger, SharedWorkflowFolder.RootFolderId, ResourceOp.Read)).Should().BeFalse();
        (await svc.GetAccessibleFolderIdsAsync(stranger)).IsUnrestricted.Should().BeFalse();
        (await svc.GetAccessibleFolderIdsAsync(stranger)).FolderIds.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAccessibleFolderIds_IncludesGrantFolderAndAllDescendants()
    {
        // Editor has FolderEditor on /Finance â€” accessible set must contain /Finance AND
        // /Finance/Reports, but NOT Root (no grant on Root) and NOT /Sales.
        var svc = NewService();
        var editor = MakePrincipal(_editorId, "Operator");

        var set = await svc.GetAccessibleFolderIdsAsync(editor);
        set.IsUnrestricted.Should().BeFalse();
        set.FolderIds.Should().Contain(_financeId);
        set.FolderIds.Should().Contain(_financeReportsId);
        set.FolderIds.Should().NotContain(SharedWorkflowFolder.RootFolderId);
        set.FolderIds.Should().NotContain(_salesId);
    }

    [Fact]
    public async Task HighestRoleWins_WhenMultipleGrantsResolveToSameUser()
    {
        // Add a SECOND grant for editorId on /Finance/Reports â€” FolderViewer (lower).
        // Despite the explicit lower grant on the child, the inherited FolderEditor from
        // /Finance must win because we collect the whole ancestry and take Max.
        _db.SharedFolderPermissions.Add(new SharedFolderPermission
        {
            Id = Guid.NewGuid(), FolderId = _financeReportsId,
            PrincipalType = FolderPrincipalType.User, PrincipalKey = _editorId.ToString("D"),
            Role = SharedFolderRole.FolderViewer,
        });
        _db.SaveChanges();

        var svc = NewService();
        var editor = MakePrincipal(_editorId, "Operator");
        (await svc.CanAccessWorkflowAsync(editor, _financeReportsId, ResourceOp.Edit))
            .Should().BeTrue("the inherited FolderEditor on /Finance must beat a direct lower grant on the child");
    }

    [Fact]
    public async Task GetWorkflowCapabilities_ReflectsRoleProgressively()
    {
        var svc = NewService();
        var editor = MakePrincipal(_editorId, "Operator");

        var caps = await svc.GetWorkflowCapabilitiesAsync(editor, _financeId);
        // Operator + FolderEditor: every workflow operation except Delete (Admin-only) and Admin (FolderAdmin only).
        caps.Should().Be(new ResourceCapabilities(CanRead: true, CanRun: true, CanEdit: true, CanDelete: false, CanAdmin: false));
    }

    [Fact]
    public async Task GetWorkflowCapabilities_CanDelete_OnlyForGlobalAdmin()
    {
        // Workflow DELETE is gated by [Authorize(Roles = "Admin")] on the controller, so
        // CanDelete may only be true for global Admins, regardless of folder RBAC. An
        // Operator with FolderEditor sees the workflow as editable but cannot delete it.
        var svc = NewService();

        var operatorEditor = MakePrincipal(_editorId, "Operator");
        var operatorCaps = await svc.GetWorkflowCapabilitiesAsync(operatorEditor, _financeId);
        operatorCaps.CanEdit.Should().BeTrue("FolderEditor grant covers edit");
        operatorCaps.CanDelete.Should().BeFalse("delete is gated on global Admin, not folder role");

        var admin = MakePrincipal(Guid.NewGuid(), "Admin");
        var adminCaps = await svc.GetWorkflowCapabilitiesAsync(admin, _financeId);
        adminCaps.CanDelete.Should().BeTrue("global Admin can delete");
    }

    [Fact]
    public async Task UnauthenticatedPrincipal_GetsNoAccess()
    {
        // ClaimsPrincipal with no NameIdentifier â€” typical for an unauthenticated request
        // that somehow reached the service. Defaults to fully-denied.
        var svc = NewService();
        var anon = new ClaimsPrincipal(new ClaimsIdentity());

        (await svc.CanAccessWorkflowAsync(anon, _financeId, ResourceOp.Read)).Should().BeFalse();
        (await svc.GetAccessibleFolderIdsAsync(anon)).IsUnrestricted.Should().BeFalse();
        (await svc.GetWorkflowCapabilitiesAsync(anon, _financeId)).Should().Be(ResourceCapabilities.None);
    }

    [Fact]
    public async Task FolderCapabilities_TreatsRunAsEdit()
    {
        // Folder-shaped resources don't have a "run" semantic â€” the service maps Runâ†’Edit
        // internally. So a FolderEditor passes a Run check on a folder.
        var svc = NewService();
        var editor = MakePrincipal(_editorId, "Operator");

        (await svc.CanAccessFolderAsync(editor, _financeId, ResourceOp.Run)).Should().BeTrue();
    }

    /// <summary>
    /// F3 fix â€” capabilities must be capped by the user's global UserRole. A global Viewer
    /// who somehow holds FolderOperator (or higher) on a folder is still globally Viewer:
    /// the controller's <c>[Authorize(Roles="Admin,Operator")]</c> would 403 any run/edit
    /// attempt, so the UI must not promise the buttons via canRun=true.
    /// </summary>
    [Fact]
    public async Task GetWorkflowCapabilities_GlobalViewer_NeverGetsCanRunOrCanEdit_EvenWithFolderOperatorGrant()
    {
        // Grant FolderOperator on /Sales to a brand-new user we'll claim as global Viewer.
        var globalViewerId = Guid.NewGuid();
        _db.SharedFolderPermissions.Add(new SharedFolderPermission
        {
            Id = Guid.NewGuid(), FolderId = _salesId,
            PrincipalType = FolderPrincipalType.User, PrincipalKey = globalViewerId.ToString("D"),
            Role = SharedFolderRole.FolderOperator,
        });
        _db.SaveChanges();

        var svc = NewService();
        var globalViewer = MakePrincipal(globalViewerId, "Viewer");

        var caps = await svc.GetWorkflowCapabilitiesAsync(globalViewer, _salesId);
        caps.CanRead.Should().BeTrue("Viewer is allowed to read");
        caps.CanRun.Should().BeFalse("global Viewer cannot run regardless of folder grants â€” the API would 403");
        caps.CanEdit.Should().BeFalse();
        caps.CanAdmin.Should().BeFalse();
    }

    [Fact]
    public async Task GetWorkflowCapabilities_GlobalOperator_GetsRunAndEdit_ButNeverAdmin()
    {
        // editor is global Operator + FolderEditor on /Finance â€” runs/edits OK, admin denied
        // because the role cap on Operator strips CanAdmin too.
        var svc = NewService();
        var editor = MakePrincipal(_editorId, "Operator");

        var caps = await svc.GetWorkflowCapabilitiesAsync(editor, _financeId);
        caps.CanRead.Should().BeTrue();
        caps.CanRun.Should().BeTrue();
        caps.CanEdit.Should().BeTrue();
        caps.CanAdmin.Should().BeFalse("global Operator never gets Admin even with FolderAdmin grant");
    }

    /// <summary>
    /// F5 fix â€” InvalidateAll must drop every cache so a capability lookup after a
    /// mutation reflects the new state.
    /// </summary>
    /// <summary>
    /// R2 â€” CanAccess* must apply the global role cap, not just GetWorkflowCapabilities.
    /// The folder/permission controllers gate via [Authorize] + _authz only (no role
    /// attribute), so without this cap a global Viewer with FolderAdmin could call
    /// grant/revoke and the API would accept it.
    /// </summary>
    [Fact]
    public async Task CanAccessWorkflow_GlobalViewer_CannotEdit_EvenWithFolderAdminGrant()
    {
        var globalViewerId = Guid.NewGuid();
        _db.SharedFolderPermissions.Add(new SharedFolderPermission
        {
            Id = Guid.NewGuid(), FolderId = _financeId,
            PrincipalType = FolderPrincipalType.User, PrincipalKey = globalViewerId.ToString("D"),
            Role = SharedFolderRole.FolderAdmin,
        });
        _db.SaveChanges();

        var svc = NewService();
        var globalViewer = MakePrincipal(globalViewerId, "Viewer");

        (await svc.CanAccessWorkflowAsync(globalViewer, _financeId, ResourceOp.Read)).Should().BeTrue(
            "Viewer can still read");
        (await svc.CanAccessWorkflowAsync(globalViewer, _financeId, ResourceOp.Run)).Should().BeFalse(
            "global Viewer must never run regardless of folder grant");
        (await svc.CanAccessWorkflowAsync(globalViewer, _financeId, ResourceOp.Edit)).Should().BeFalse();
        (await svc.CanAccessWorkflowAsync(globalViewer, _financeId, ResourceOp.Admin)).Should().BeFalse(
            "global Viewer with FolderAdmin must NOT be able to grant â€” closes the API/UI consistency gap");
    }

    [Fact]
    public async Task CanAccessFolder_GlobalOperator_CannotAdmin_EvenWithFolderAdminGrant()
    {
        var operatorId = Guid.NewGuid();
        _db.SharedFolderPermissions.Add(new SharedFolderPermission
        {
            Id = Guid.NewGuid(), FolderId = _financeId,
            PrincipalType = FolderPrincipalType.User, PrincipalKey = operatorId.ToString("D"),
            Role = SharedFolderRole.FolderAdmin,
        });
        _db.SaveChanges();

        var svc = NewService();
        var op = MakePrincipal(operatorId, "Operator");

        (await svc.CanAccessFolderAsync(op, _financeId, ResourceOp.Edit)).Should().BeTrue(
            "Operator with FolderAdmin can edit folders in scope");
        (await svc.CanAccessFolderAsync(op, _financeId, ResourceOp.Admin)).Should().BeFalse(
            "global Operator never gets Admin (grant/revoke) â€” capped by global role");
    }

    [Fact]
    public async Task InvalidateAll_ResetsAccessibleSetCache()
    {
        var svc = NewService();
        var editor = MakePrincipal(_editorId, "Operator");

        // Warm the cache.
        var first = await svc.GetAccessibleFolderIdsAsync(editor);
        first.FolderIds.Should().Contain(_financeId);

        // Add a brand-new grant out-of-band.
        var newFolderId = Guid.NewGuid();
        _db.SharedWorkflowFolders.Add(new SharedWorkflowFolder
        {
            Id = newFolderId,
            ParentFolderId = SharedWorkflowFolder.RootFolderId,
            Name = "HR", Path = "/HR", Depth = 1,
        });
        _db.SharedFolderPermissions.Add(new SharedFolderPermission
        {
            Id = Guid.NewGuid(), FolderId = newFolderId,
            PrincipalType = FolderPrincipalType.User, PrincipalKey = _editorId.ToString("D"),
            Role = SharedFolderRole.FolderEditor,
        });
        _db.SaveChanges();

        // Without InvalidateAll, the cache would still return the old set.
        var stillCached = await svc.GetAccessibleFolderIdsAsync(editor);
        stillCached.FolderIds.Should().NotContain(newFolderId, "before InvalidateAll, cache is stale by design");

        svc.InvalidateAll();
        var fresh = await svc.GetAccessibleFolderIdsAsync(editor);
        fresh.FolderIds.Should().Contain(newFolderId, "InvalidateAll must drop the cache so the next lookup re-reads");
    }
}
