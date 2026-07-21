using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.Security;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Rbac;

/// <summary>
/// Verifies that <see cref="WorkflowsController.GetAll"/> + <see cref="WorkflowsController.GetById"/>
/// ship per-row <see cref="WorkflowCapabilities"/> in the DTO so the UI doesn't have to
/// infer them from the global role.
/// </summary>
public sealed class WorkflowResponseCapabilitiesTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _conn;
    private readonly Data.NodePilotDbContext _db;
    private readonly Guid _financeId = Guid.NewGuid();
    private readonly Guid _viewerId = Guid.NewGuid();
    private readonly Guid _editorId = Guid.NewGuid();
    private readonly Workflow _financeWorkflow;

    public WorkflowResponseCapabilitiesTests()
    {
        var (conn, db) = TestDbFactory.CreateWithConnection();
        _conn = conn;
        _db = db;

        _db.SharedWorkflowFolders.Add(new SharedWorkflowFolder
        {
            Id = _financeId, ParentFolderId = SharedWorkflowFolder.RootFolderId,
            Name = "Finance", Path = "/Finance", Depth = 1
        });
        _db.SharedFolderPermissions.AddRange(
            new SharedFolderPermission
            {
                Id = Guid.NewGuid(), FolderId = _financeId,
                PrincipalType = FolderPrincipalType.User, PrincipalKey = _editorId.ToString("D"),
                Role = SharedFolderRole.FolderEditor,
            },
            new SharedFolderPermission
            {
                Id = Guid.NewGuid(), FolderId = _financeId,
                PrincipalType = FolderPrincipalType.User, PrincipalKey = _viewerId.ToString("D"),
                Role = SharedFolderRole.FolderViewer,
            });
        _financeWorkflow = new Workflow
        {
            Id = Guid.NewGuid(), Name = "wf", DefinitionJson = "{}", FolderId = _financeId,
            Version = 1, IsEnabled = true,
        };
        _db.Workflows.Add(_financeWorkflow);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private WorkflowsController NewCtrl(Guid userId, string role)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, role),
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
    public async Task GetById_AsEditor_HasFullCapabilitiesExceptAdmin()
    {
        var ctrl = NewCtrl(_editorId, "Operator");
        var ok = (await ctrl.GetById(_financeWorkflow.Id, CancellationToken.None)).Result as OkObjectResult;
        var dto = ok!.Value as WorkflowResponse;
        // Operator with FolderEditor: Read+Run+Edit, but Delete is Admin-only at the controller
        // and Admin grant is folder-Admin only — both false here.
        dto!.Capabilities.Should().Be(new WorkflowCapabilities(true, true, true, false, false));
        dto.FolderId.Should().Be(_financeId);
        dto.FolderPath.Should().Be("/Finance");
    }

    [Fact]
    public async Task GetById_AsViewer_HasOnlyReadCapability()
    {
        var ctrl = NewCtrl(_viewerId, "Viewer");
        var ok = (await ctrl.GetById(_financeWorkflow.Id, CancellationToken.None)).Result as OkObjectResult;
        var dto = ok!.Value as WorkflowResponse;
        dto!.Capabilities.Should().Be(new WorkflowCapabilities(true, false, false, false, false));
    }

    [Fact]
    public async Task GetById_AsGlobalOperatorWithFolderViewer_RedactsInlineSecrets()
    {
        _financeWorkflow.DefinitionJson =
            """{"nodes":[{"id":"http","data":{"config":{"apiKey":"TOP-SECRET"}}}],"edges":[]}""";
        _db.SaveChanges();

        // The global role is only a cap. FolderViewer does not grant Edit, so it must
        // not accidentally grant the raw-definition/secret capability either.
        var ctrl = NewCtrl(_viewerId, "Operator");
        var ok = (await ctrl.GetById(_financeWorkflow.Id, CancellationToken.None)).Result as OkObjectResult;
        var dto = ok!.Value as WorkflowResponse;

        dto!.Capabilities.Should().Be(new WorkflowCapabilities(true, false, false, false, false));
        dto.DefinitionJson.Should().NotContain("TOP-SECRET");
        dto.DefinitionJson.Should().Contain("***");
    }

    [Fact]
    public async Task GetById_AsAdmin_HasAllCapabilities()
    {
        var ctrl = NewCtrl(Guid.NewGuid(), "Admin");
        var ok = (await ctrl.GetById(_financeWorkflow.Id, CancellationToken.None)).Result as OkObjectResult;
        var dto = ok!.Value as WorkflowResponse;
        // Global Admin: has every capability, including CanDelete.
        dto!.Capabilities.Should().Be(new WorkflowCapabilities(true, true, true, true, true));
    }

    [Fact]
    public async Task GetAll_PerRow_CapabilitiesReflectFolderRole()
    {
        // Add a Sales workflow + Sales folder where editor has no grant â€” list response
        // should NOT contain Sales for the editor.
        var salesId = Guid.NewGuid();
        _db.SharedWorkflowFolders.Add(new SharedWorkflowFolder
        {
            Id = salesId, ParentFolderId = SharedWorkflowFolder.RootFolderId,
            Name = "Sales", Path = "/Sales", Depth = 1
        });
        _db.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(), Name = "swf", DefinitionJson = "{}", FolderId = salesId,
            Version = 1, IsEnabled = true,
        });
        _db.SaveChanges();

        var ctrl = NewCtrl(_editorId, "Operator");
        var ok = (await ctrl.GetAll(CancellationToken.None)).Result as OkObjectResult;
        var list = ok!.Value as List<WorkflowResponse>;
        list.Should().HaveCount(1, "editor sees only Finance, not Sales");
        list![0].Capabilities.CanEdit.Should().BeTrue();
        list[0].FolderPath.Should().Be("/Finance");
    }
}
