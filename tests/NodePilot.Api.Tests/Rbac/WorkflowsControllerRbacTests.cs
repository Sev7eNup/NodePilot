using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Api.Controllers;
using NodePilot.Api.Security;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Rbac;

/// <summary>
/// End-to-end RBAC-denial tests for <see cref="WorkflowsController"/>. Built on the real
/// <see cref="ResourceAuthorizationService"/> (not a mock) so the inheritance walk runs
/// the same code as production. Folder tree:
///   Root
///   â”œâ”€â”€ Finance         (op-finance has FolderEditor)
///   â”‚    â””â”€â”€ Reports
///   â””â”€â”€ Sales           (no per-test grant â€” used for sibling-isolation checks)
/// </summary>
public sealed class WorkflowsControllerRbacTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _conn;
    private readonly Data.NodePilotDbContext _db;
    private readonly Guid _financeId = Guid.NewGuid();
    private readonly Guid _financeReportsId = Guid.NewGuid();
    private readonly Guid _salesId = Guid.NewGuid();
    private readonly Guid _opFinanceUserId = Guid.NewGuid();
    private readonly Guid _viewerEverywhereUserId = Guid.NewGuid();
    private readonly Guid _strangerUserId = Guid.NewGuid();
    private readonly Workflow _financeWorkflow;
    private readonly Workflow _salesWorkflow;

    public WorkflowsControllerRbacTests()
    {
        var (conn, db) = TestDbFactory.CreateWithConnection();
        _conn = conn;
        _db = db;

        var rootId = SharedWorkflowFolder.RootFolderId;
        _db.SharedWorkflowFolders.AddRange(
            new SharedWorkflowFolder { Id = _financeId, ParentFolderId = rootId, Name = "Finance", Path = "/Finance", Depth = 1 },
            new SharedWorkflowFolder { Id = _financeReportsId, ParentFolderId = _financeId, Name = "Reports", Path = "/Finance/Reports", Depth = 2 },
            new SharedWorkflowFolder { Id = _salesId, ParentFolderId = rootId, Name = "Sales", Path = "/Sales", Depth = 1 });
        _db.SharedFolderPermissions.Add(new SharedFolderPermission
        {
            Id = Guid.NewGuid(), FolderId = _financeId,
            PrincipalType = FolderPrincipalType.User, PrincipalKey = _opFinanceUserId.ToString("D"),
            Role = SharedFolderRole.FolderEditor,
        });
        _db.SharedFolderPermissions.Add(new SharedFolderPermission
        {
            Id = Guid.NewGuid(), FolderId = rootId,
            PrincipalType = FolderPrincipalType.User, PrincipalKey = _viewerEverywhereUserId.ToString("D"),
            Role = SharedFolderRole.FolderViewer,
        });

        _financeWorkflow = new Workflow
        {
            Id = Guid.NewGuid(), Name = "finance-wf", DefinitionJson = "{}", FolderId = _financeId,
            Version = 1, IsEnabled = true,
        };
        _salesWorkflow = new Workflow
        {
            Id = Guid.NewGuid(), Name = "sales-wf", DefinitionJson = "{}", FolderId = _salesId,
            Version = 1, IsEnabled = true,
        };
        _db.Workflows.AddRange(_financeWorkflow, _salesWorkflow);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private WorkflowsController NewController(Guid userId, string globalRole)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, globalRole),
        ], "test"));
        var ctrl = new WorkflowsController(
            _db, NullLogger<WorkflowsController>.Instance, NoopAuditWriter.Instance,
            new ResourceAuthorizationService(_db),
            new NodePilot.Api.Services.WorkflowContractDeriver())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } }
        };
        return ctrl;
    }

    [Fact]
    public async Task GetById_AsStranger_Returns404_NotForbid_DoesNotLeakExistence()
    {
        var ctrl = NewController(_strangerUserId, "Operator");
        var result = await ctrl.GetById(_financeWorkflow.Id, CancellationToken.None);
        result.Result.Should().BeOfType<NotFoundResult>(
            "a stranger must not be able to distinguish 'workflow exists but I lack rights' from 'workflow does not exist'");
    }

    [Fact]
    public async Task GetById_AsFinanceEditor_Succeeds()
    {
        var ctrl = NewController(_opFinanceUserId, "Operator");
        var result = await ctrl.GetById(_financeWorkflow.Id, CancellationToken.None);
        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetById_AsFinanceEditor_OnSalesWorkflow_Returns404()
    {
        var ctrl = NewController(_opFinanceUserId, "Operator");
        var result = await ctrl.GetById(_salesWorkflow.Id, CancellationToken.None);
        result.Result.Should().BeOfType<NotFoundResult>(
            "a FolderEditor on /Finance must not see workflows in /Sales â€” folder isolation");
    }

    [Fact]
    public async Task GetByName_InaccessibleDuplicate_DoesNotLeakOrCauseAmbiguity()
    {
        _salesWorkflow.Name = _financeWorkflow.Name;
        _db.SaveChanges();
        var ctrl = NewController(_opFinanceUserId, "Operator");

        var workflowResult = await ctrl.GetByName(_financeWorkflow.Name, CancellationToken.None);
        var contractResult = await ctrl.GetContractByName(_financeWorkflow.Name, CancellationToken.None);

        workflowResult.Result.Should().BeOfType<OkObjectResult>();
        contractResult.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetAll_AsViewerEverywhere_ReturnsAllWorkflows()
    {
        // Viewer has FolderViewer on Root â†’ inherited Read on every folder.
        var ctrl = NewController(_viewerEverywhereUserId, "Viewer");
        var result = await ctrl.GetAll(CancellationToken.None);
        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var list = ok!.Value as List<Dtos.WorkflowResponse>;
        list.Should().HaveCount(2, "Viewer-on-Root sees both /Finance and /Sales workflows via inheritance");
    }

    [Fact]
    public async Task GetAll_AsFinanceEditor_ReturnsOnlyFinanceTree()
    {
        var ctrl = NewController(_opFinanceUserId, "Operator");
        var result = await ctrl.GetAll(CancellationToken.None);
        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var list = ok!.Value as List<Dtos.WorkflowResponse>;
        list.Should().HaveCount(1);
        list![0].Id.Should().Be(_financeWorkflow.Id, "the Sales workflow must be filtered out");
    }

    [Fact]
    public async Task GetAll_AsStranger_ReturnsEmptyList()
    {
        var ctrl = NewController(_strangerUserId, "Operator");
        var result = await ctrl.GetAll(CancellationToken.None);
        var ok = result.Result as OkObjectResult;
        var list = ok!.Value as List<Dtos.WorkflowResponse>;
        list.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAll_AsAdmin_ReturnsAllWorkflows_BypassesFolderFilter()
    {
        // Admin has no explicit grants â€” global role bypasses every folder check.
        var adminId = Guid.NewGuid();
        var ctrl = NewController(adminId, "Admin");
        var result = await ctrl.GetAll(CancellationToken.None);
        var ok = result.Result as OkObjectResult;
        var list = ok!.Value as List<Dtos.WorkflowResponse>;
        list.Should().HaveCount(2);
    }

    [Fact]
    public async Task Delete_AsViewerEverywhere_OnFinanceWorkflow_Returns403()
    {
        // Viewer has Read but not Edit on any folder â€” Delete must be 403, NOT 404
        // (the user CAN see the workflow via Read).
        var ctrl = NewController(_viewerEverywhereUserId, "Operator");
        var result = await ctrl.Delete(_financeWorkflow.Id, CancellationToken.None);
        var obj = result as ObjectResult;
        obj.Should().NotBeNull("403 returned via ObjectResult with status 403");
        obj!.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task Delete_AsStranger_Returns404_NotForbid()
    {
        var ctrl = NewController(_strangerUserId, "Operator");
        var result = await ctrl.Delete(_financeWorkflow.Id, CancellationToken.None);
        result.Should().BeOfType<NotFoundResult>(
            "stranger has no Read access â€” must mask existence with 404 even on mutating endpoint");
    }

    /// <summary>
    /// Regression test for a fixed folder-permission gap (internally tracked as "F2") — Create
    /// must check Edit permission on the target folder. A stranger with no grant on Root cannot
    /// drop a workflow into Root just by hitting POST /api/workflows.
    /// </summary>
    [Fact]
    public async Task Create_AsStranger_TargetingRoot_Returns404_BecauseStrangerCannotEvenSeeRoot()
    {
        var ctrl = NewController(_strangerUserId, "Operator");
        var req = new NodePilot.Api.Dtos.CreateWorkflowRequest(
            "new-wf", null, "{\"nodes\":[],\"edges\":[]}", FolderId: null);

        var result = await ctrl.Create(req, CancellationToken.None);
        result.Result.Should().BeOfType<NotFoundResult>(
            "without Read on Root, the stranger cannot even target it â€” folder is masked as 404 (existence-leak prevention)");
    }

    [Fact]
    public async Task Create_AsStranger_TargetingUnknownFolder_IsAlsoMaskedAs404()
    {
        var ctrl = NewController(_strangerUserId, "Operator");
        var req = new NodePilot.Api.Dtos.CreateWorkflowRequest(
            "new-wf", null, "{\"nodes\":[],\"edges\":[]}", FolderId: Guid.NewGuid());

        var result = await ctrl.Create(req, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>(
            "an inaccessible existing folder and an unknown folder must be indistinguishable");
    }

    [Fact]
    public async Task Create_AsViewer_TargetingFinance_Returns403()
    {
        // viewerEverywhere has FolderViewer on Root â†’ inherits Read on /Finance but NOT Edit.
        // Targeting /Finance with Create must yield 403.
        var ctrl = NewController(_viewerEverywhereUserId, "Operator");
        var req = new NodePilot.Api.Dtos.CreateWorkflowRequest(
            "new-wf", null, "{\"nodes\":[],\"edges\":[]}", FolderId: _financeId);

        var result = await ctrl.Create(req, CancellationToken.None);
        var obj = result.Result as ObjectResult;
        obj.Should().NotBeNull();
        obj!.StatusCode.Should().Be(403,
            "FolderViewer can read /Finance but cannot create new workflows there â€” must be 403");
    }

    [Fact]
    public async Task Create_AsFinanceEditor_TargetingFinance_Succeeds_AndPersistsFolderId()
    {
        var ctrl = NewController(_opFinanceUserId, "Operator");
        var req = new NodePilot.Api.Dtos.CreateWorkflowRequest(
            "wf-in-finance", null, "{\"nodes\":[],\"edges\":[]}", FolderId: _financeId);

        var result = await ctrl.Create(req, CancellationToken.None);
        result.Result.Should().BeOfType<CreatedAtActionResult>();
        var stored = _db.Workflows.AsNoTracking().Single(w => w.Name == "wf-in-finance");
        stored.FolderId.Should().Be(_financeId, "Create must persist the requested target folder");
    }

    [Fact]
    public async Task Create_NullFolderId_DefaultsToRoot()
    {
        // _opFinanceUserId has no Root grant — null should default to Root and then be denied
        // by the Edit check on Root, mirroring how a "default to root" bug would slip past the
        // folder-permission check added for the "F2" fix above.
        var ctrl = NewController(_opFinanceUserId, "Operator");
        var req = new NodePilot.Api.Dtos.CreateWorkflowRequest(
            "should-not-land", null, "{\"nodes\":[],\"edges\":[]}", FolderId: null);

        var result = await ctrl.Create(req, CancellationToken.None);
        // Expect not-found because no Read on Root either (FolderEditor was scoped to /Finance).
        result.Result.Should().BeOfType<NotFoundResult>(
            "default-to-Root + missing Root grant must NOT silently land in Root â€” must be masked");
    }
}
