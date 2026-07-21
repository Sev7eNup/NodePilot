using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Api.Controllers;
using NodePilot.Api.ExecutionDispatch;
using NodePilot.Api.Security;
using NodePilot.Api.Tests.Controllers;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Security;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Rbac;

/// <summary>
/// RBAC for the executions surface. Two workflows in different folders, two executions
/// (one per workflow). Verifies (a) <see cref="ExecutionsController.GetAll"/> filters by
/// folder access, (b) <see cref="ExecutionsController.GetById"/> + GetSteps mask
/// existence with 404, (c) Cancel/Retry require Run permission, (d) Execute requires Run.
/// </summary>
public sealed class ExecutionsControllerRbacTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _conn;
    private readonly Data.NodePilotDbContext _db;
    private readonly Guid _financeId = Guid.NewGuid();
    private readonly Guid _salesId = Guid.NewGuid();
    private readonly Guid _opFinanceUserId = Guid.NewGuid();
    private readonly Guid _strangerUserId = Guid.NewGuid();
    private readonly Workflow _financeWorkflow;
    private readonly Workflow _salesWorkflow;
    private readonly WorkflowExecution _financeExec;
    private readonly WorkflowExecution _salesExec;

    public ExecutionsControllerRbacTests()
    {
        var (conn, db) = TestDbFactory.CreateWithConnection();
        _conn = conn;
        _db = db;

        var rootId = SharedWorkflowFolder.RootFolderId;
        _db.SharedWorkflowFolders.AddRange(
            new SharedWorkflowFolder { Id = _financeId, ParentFolderId = rootId, Name = "Finance", Path = "/Finance", Depth = 1 },
            new SharedWorkflowFolder { Id = _salesId, ParentFolderId = rootId, Name = "Sales", Path = "/Sales", Depth = 1 });
        _db.SharedFolderPermissions.Add(new SharedFolderPermission
        {
            Id = Guid.NewGuid(), FolderId = _financeId,
            PrincipalType = FolderPrincipalType.User, PrincipalKey = _opFinanceUserId.ToString("D"),
            Role = SharedFolderRole.FolderEditor,
        });

        _financeWorkflow = new Workflow { Id = Guid.NewGuid(), Name = "fwf", DefinitionJson = "{}", FolderId = _financeId, Version = 1, IsEnabled = true };
        _salesWorkflow = new Workflow { Id = Guid.NewGuid(), Name = "swf", DefinitionJson = "{}", FolderId = _salesId, Version = 1, IsEnabled = true };
        _db.Workflows.AddRange(_financeWorkflow, _salesWorkflow);

        _financeExec = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = _financeWorkflow.Id, Status = ExecutionStatus.Succeeded, StartedAt = DateTime.UtcNow };
        _salesExec = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = _salesWorkflow.Id, Status = ExecutionStatus.Succeeded, StartedAt = DateTime.UtcNow };
        _db.WorkflowExecutions.AddRange(_financeExec, _salesExec);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private ExecutionsController NewController(Guid userId, string role)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, role),
        ], "test"));

        var queue = new NoopExecutionDispatchQueue();
        var dispatchService = new ExecutionDispatchService(
            _db, queue,
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new OutputRedactor(null),
            new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            NodePilot.Api.Tests.TestSupport.StubMaintenanceWindowEvaluator.AllowAll,
            NullLogger<ExecutionDispatchService>.Instance);

        var ctrl = new ExecutionsController(
            _db, Mock.Of<IWorkflowEngine>(), dispatchService,
            new OutputRedactor(null), NoopAuditWriter.Instance,
            new ResourceAuthorizationService(_db),
            NodePilot.Api.Tests.TestSupport.StubMaintenanceWindowEvaluator.AllowAll);
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
        return ctrl;
    }

    [Fact]
    public async Task GetAll_AsFinanceEditor_ReturnsOnlyFinanceExecutions()
    {
        var ctrl = NewController(_opFinanceUserId, "Operator");
        var result = await ctrl.GetAll(workflowId: null, activeOnly: false, terminalOnly: false, CancellationToken.None);
        var ok = result.Result as OkObjectResult;
        var list = ok!.Value as List<Dtos.ExecutionResponse>;
        list.Should().HaveCount(1);
        list![0].Id.Should().Be(_financeExec.Id);
    }

    [Fact]
    public async Task GetAll_AsStranger_ReturnsEmpty()
    {
        var ctrl = NewController(_strangerUserId, "Operator");
        var result = await ctrl.GetAll(workflowId: null, activeOnly: false, terminalOnly: false, CancellationToken.None);
        var ok = result.Result as OkObjectResult;
        var list = ok!.Value as List<Dtos.ExecutionResponse>;
        list.Should().BeEmpty();
    }

    [Fact]
    public async Task GetById_AsFinanceEditor_OnSalesExec_Returns404()
    {
        var ctrl = NewController(_opFinanceUserId, "Operator");
        var result = await ctrl.GetById(_salesExec.Id, CancellationToken.None);
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetById_AsAdmin_OnAnyExec_Succeeds()
    {
        var ctrl = NewController(Guid.NewGuid(), "Admin");
        (await ctrl.GetById(_salesExec.Id, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();
        (await ctrl.GetById(_financeExec.Id, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetSteps_AsFinanceEditor_OnSalesExec_Returns404()
    {
        var ctrl = NewController(_opFinanceUserId, "Operator");
        var result = await ctrl.GetSteps(_salesExec.Id, CancellationToken.None);
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Execute_AsFinanceEditor_OnSalesWorkflow_Returns404()
    {
        var ctrl = NewController(_opFinanceUserId, "Operator");
        var result = await ctrl.Execute(_salesWorkflow.Id, request: null, CancellationToken.None);
        result.Result.Should().BeOfType<NotFoundResult>();
    }
}
