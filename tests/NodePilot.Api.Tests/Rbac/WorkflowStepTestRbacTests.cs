using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Api.Audit;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.Security;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Rbac;

public sealed class WorkflowStepTestRbacTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly NodePilotDbContext _db;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _folderId = Guid.NewGuid();
    private readonly Workflow _workflow;
    private readonly Mock<IStepTester> _stepTester = new();

    public WorkflowStepTestRbacTests()
    {
        (_connection, _db) = TestDbFactory.CreateWithConnection();

        _db.SharedWorkflowFolders.Add(new SharedWorkflowFolder
        {
            Id = _folderId,
            ParentFolderId = SharedWorkflowFolder.RootFolderId,
            Name = "Operations",
            Path = "/Operations",
            Depth = 1,
        });
        _db.SharedFolderPermissions.Add(new SharedFolderPermission
        {
            Id = Guid.NewGuid(),
            FolderId = _folderId,
            PrincipalType = FolderPrincipalType.User,
            PrincipalKey = _userId.ToString("D"),
            Role = SharedFolderRole.FolderOperator,
        });
        _workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "production-script",
            DefinitionJson = "{\"nodes\":[],\"edges\":[]}",
            FolderId = _folderId,
            CheckedOutByUserId = _userId,
            CheckedOutAt = DateTime.UtcNow,
        };
        _db.Workflows.Add(_workflow);
        _db.SaveChanges();

        _stepTester
            .Setup(x => x.TestStepAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<StepTestAuthorizationSnapshot>(),
                It.IsAny<Dictionary<string, string>?>(),
                It.IsAny<JsonElement?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StepTestResult(true, "ok", null, [], 1, null));
    }

    [Fact]
    public async Task ConfigOverride_AsFolderOperator_IsForbiddenWithoutExecutingStep()
    {
        var controller = NewController();
        var configOverride = JsonDocument.Parse("""{"script":"Write-Output pwned"}""").RootElement.Clone();

        var result = await controller.TestStep(
            _workflow.Id,
            "run-script",
            new StepTestRequest(ConfigOverride: configOverride),
            CancellationToken.None);

        var denied = result.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        denied.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        _stepTester.Verify(x => x.TestStepAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<StepTestAuthorizationSnapshot>(),
            It.IsAny<Dictionary<string, string>?>(),
            It.IsAny<JsonElement?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PersistedConfig_AsFolderOperator_ExecutesWithRunPermission()
    {
        var controller = NewController();

        var result = await controller.TestStep(
            _workflow.Id,
            "persisted-step",
            new StepTestRequest(),
            CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        _stepTester.Verify(x => x.TestStepAsync(
            _workflow.Id,
            "persisted-step",
            It.Is<StepTestAuthorizationSnapshot>(snapshot =>
                snapshot.FolderId == _folderId
                && snapshot.Version == _workflow.Version
                && snapshot.CheckedOutByUserId == _userId
                && snapshot.CheckedOutAt == _workflow.CheckedOutAt),
            null,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConfigOverride_AsFolderEditorWithoutLock_ReturnsLockedWithoutExecutingStep()
    {
        _db.SharedFolderPermissions.Single().Role = SharedFolderRole.FolderEditor;
        _workflow.CheckedOutByUserId = null;
        _workflow.CheckedOutAt = null;
        await _db.SaveChangesAsync();
        var controller = NewController();
        var configOverride = JsonDocument.Parse("""{"seconds":0}""").RootElement.Clone();

        var result = await controller.TestStep(
            _workflow.Id,
            "delay",
            new StepTestRequest(ConfigOverride: configOverride),
            CancellationToken.None);

        var denied = result.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        denied.StatusCode.Should().Be(StatusCodes.Status423Locked);
        _stepTester.Verify(x => x.TestStepAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<StepTestAuthorizationSnapshot>(),
            It.IsAny<Dictionary<string, string>?>(),
            It.IsAny<JsonElement?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConfigOverride_AsLockOwningFolderEditor_ExecutesOverride()
    {
        _db.SharedFolderPermissions.Single().Role = SharedFolderRole.FolderEditor;
        await _db.SaveChangesAsync();
        var controller = NewController();
        var configOverride = JsonDocument.Parse("""{"seconds":0}""").RootElement.Clone();

        var result = await controller.TestStep(
            _workflow.Id,
            "delay",
            new StepTestRequest(ConfigOverride: configOverride),
            CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        _stepTester.Verify(x => x.TestStepAsync(
            _workflow.Id,
            "delay",
            It.Is<StepTestAuthorizationSnapshot>(snapshot =>
                snapshot.FolderId == _folderId
                && snapshot.Version == _workflow.Version
                && snapshot.CheckedOutByUserId == _userId
                && snapshot.CheckedOutAt == _workflow.CheckedOutAt),
            null,
            It.Is<JsonElement?>(value => value.HasValue && value.Value.GetProperty("seconds").GetInt32() == 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private WorkflowEditingController NewController()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, _userId.ToString()),
            new Claim(ClaimTypes.Name, "folder-operator"),
            new Claim(ClaimTypes.Role, "Operator"),
        ], "test"));

        return new WorkflowEditingController(
            _db,
            NullLogger<WorkflowEditingController>.Instance,
            NoopAuditWriter.Instance,
            new ResourceAuthorizationService(_db),
            _stepTester.Object,
            Mock.Of<IStepTestContextProvider>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            },
        };
    }
}
