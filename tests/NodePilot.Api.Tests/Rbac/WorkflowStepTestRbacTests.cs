using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.Security;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine;
using NodePilot.TestCommons;
using NodePilot.Api.Tests.TestSupport;
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
                It.IsAny<JsonElement?>(), It.IsAny<CancellationToken>(), It.IsAny<JsonElement?>()))
            .ReturnsAsync(new StepTestResult(true, "ok", null, [], 1, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Override_AsFolderOperator_IsForbiddenWithoutExecutingStep(bool completeStep)
    {
        var controller = NewController();
        var configOverride = JsonDocument.Parse("""{"script":"Write-Output pwned"}""").RootElement.Clone();

        var result = await controller.TestStep(
            _workflow.Id,
            "run-script",
            OverrideRequest(completeStep, configOverride),
            CancellationToken.None);

        var denied = result.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        denied.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        _stepTester.Verify(x => x.TestStepAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<StepTestAuthorizationSnapshot>(),
            It.IsAny<Dictionary<string, string>?>(),
            It.IsAny<JsonElement?>(), It.IsAny<CancellationToken>(), It.IsAny<JsonElement?>()), Times.Never);
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
            It.IsAny<CancellationToken>(), null), Times.Once);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Override_AsFolderEditorWithoutOwnLock_ReturnsLockedWithoutExecutingStep(bool completeStep, bool foreignLock)
    {
        _db.SharedFolderPermissions.Single().Role = SharedFolderRole.FolderEditor;
        _workflow.CheckedOutByUserId = foreignLock ? Guid.NewGuid() : null;
        _workflow.CheckedOutAt = foreignLock ? DateTime.UtcNow : null;
        await _db.SaveChangesAsync();
        var controller = NewController();
        var configOverride = JsonDocument.Parse("""{"seconds":0}""").RootElement.Clone();

        var result = await controller.TestStep(
            _workflow.Id,
            "delay",
            OverrideRequest(completeStep, configOverride),
            CancellationToken.None);

        var denied = result.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        denied.StatusCode.Should().Be(StatusCodes.Status423Locked);
        _stepTester.Verify(x => x.TestStepAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<StepTestAuthorizationSnapshot>(),
            It.IsAny<Dictionary<string, string>?>(),
            It.IsAny<JsonElement?>(), It.IsAny<CancellationToken>(), It.IsAny<JsonElement?>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Override_AsLockOwningFolderEditor_ExecutesOverride(bool completeStep)
    {
        _db.SharedFolderPermissions.Single().Role = SharedFolderRole.FolderEditor;
        await _db.SaveChangesAsync();
        var controller = NewController();
        var configOverride = JsonDocument.Parse("""{"seconds":0}""").RootElement.Clone();

        var result = await controller.TestStep(
            _workflow.Id,
            "delay",
            OverrideRequest(completeStep, configOverride),
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
            It.Is<JsonElement?>(value => completeStep ? !value.HasValue : value.HasValue && value.Value.GetProperty("seconds").GetInt32() == 0),
            It.IsAny<CancellationToken>(),
            It.Is<JsonElement?>(value => completeStep ? value.HasValue && value.Value.GetProperty("config").GetProperty("seconds").GetInt32() == 0 : !value.HasValue)), Times.Once);
    }

    [Fact]
    public async Task CombinedOverrides_ReturnsBadRequestWithoutExecutingStep()
    {
        var ct = TestContext.Current.CancellationToken;
        _db.SharedFolderPermissions.Single().Role = SharedFolderRole.FolderEditor;
        await _db.SaveChangesAsync(ct);
        var config = JsonSerializer.SerializeToElement(new { seconds = 0 });
        var request = OverrideRequest(true, config) with { ConfigOverride = config };

        var result = await NewController().TestStep(_workflow.Id, "delay", request, ct);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        _stepTester.VerifyNoOtherCalls();
    }

    private static StepTestRequest OverrideRequest(bool completeStep, JsonElement config)
        => completeStep
            ? new StepTestRequest(StepOverride: JsonSerializer.SerializeToElement(new { activityType = "delay", config }))
            : new StepTestRequest(ConfigOverride: config);

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
            Mock.Of<IStepTestContextProvider>(),
            NodePilot.Api.Tests.Controllers.WorkflowControllerHarnessFactory.VersionDefinitions())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            },
        };
    }
}
