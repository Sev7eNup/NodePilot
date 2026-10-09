using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Api.Controllers;
using NodePilot.Api.Hubs;
using NodePilot.Api.Security;
using NodePilot.Api.Services.DbAdmin;
using NodePilot.Api.Tests.Hubs;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

[Collection(ExecutionHubStaticStateCollection.Name)]
public sealed class DbAdminWorkflowMoveTests : IDisposable
{
    public DbAdminWorkflowMoveTests() => Clear();
    public void Dispose() => Clear();
    private static void Clear()
    {
        ExecutionHub.ClearGroupsForTest();
        ExecutionHub.ClearOpsFeedForTest();
        ExecutionHub.ClearAuthMapForTest();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CellPatch_FolderMoveRevokesExistingReader_ButNameEditKeepsSubscription(bool move)
    {
        await using var db = TestDbFactory.Create();
        var source = new SharedWorkflowFolder
        {
            Id = Guid.NewGuid(), Name = "Source", Path = "/Source", Depth = 1,
            ParentFolderId = SharedWorkflowFolder.RootFolderId,
        };
        var target = new SharedWorkflowFolder
        {
            Id = Guid.NewGuid(), Name = "Private", Path = "/Private", Depth = 1,
            ParentFolderId = SharedWorkflowFolder.RootFolderId,
        };
        var viewer = new User { Id = Guid.NewGuid(), Username = "local-viewer", Role = UserRole.Viewer };
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "Running workflow", FolderId = source.Id };
        var execution = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = workflow.Id, Status = ExecutionStatus.Running };
        db.AddRange(source, target, viewer, workflow, execution, new SharedFolderPermission
        {
            Id = Guid.NewGuid(), FolderId = source.Id, PrincipalType = FolderPrincipalType.User,
            PrincipalKey = viewer.Id.ToString(), Role = SharedFolderRole.FolderViewer,
        });
        await db.SaveChangesAsync();

        var caller = new Mock<HubCallerContext>();
        caller.SetupGet(c => c.ConnectionId).Returns("local-reader");
        caller.SetupGet(c => c.User).Returns(Principal(viewer.Id, UserRole.Viewer));
        var groups = new Mock<IGroupManager>();
        groups.Setup(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        groups.Setup(g => g.RemoveFromGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Groups(It.IsAny<IReadOnlyList<string>>())).Returns(proxy.Object);
        var hub = new Mock<IHubContext<ExecutionHub>>();
        hub.SetupGet(h => h.Groups).Returns(groups.Object);
        hub.SetupGet(h => h.Clients).Returns(clients.Object);
        using var notifier = new SignalRExecutionNotifier(hub.Object);
        var reader = new ExecutionHub(db, new ResourceAuthorizationService(db)) { Context = caller.Object, Groups = groups.Object };
        await reader.JoinWorkflow(workflow.Id.ToString());
        await reader.JoinExecution(execution.Id.ToString());
        await notifier.StepCompletedAsync(execution.Id, workflow.Id, "step", null, ExecutionStatus.Succeeded, "before", null, DateTime.UtcNow);
        await notifier.FlushForTestAsync();
        proxy.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once());
        proxy.Invocations.Clear();

        using var provider = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
        var metadata = new DbAdminMetadataService(provider.GetRequiredService<IServiceScopeFactory>());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = new DbAdminController(db, metadata,
            new DbAdminQueryExecutor(db, new StaticOptionsMonitor<DbAdminOptions>(new())),
            new DbAdminSecretColumns(metadata), new AuditStager(), cache, NullLogger<DbAdminController>.Instance,
            hub.Object, notifier)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(Guid.NewGuid(), UserRole.Admin) } },
        };
        var patch = new DbAdminPatchRequest(move ? nameof(Workflow.FolderId) : nameof(Workflow.Name),
            JsonSerializer.SerializeToElement(move ? target.Id.ToString() : "Renamed"));
        (await controller.PatchRow("Workflow", [workflow.Id.ToString()], patch, default)).Should().BeOfType<NoContentResult>();
        var saved = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == workflow.Id);
        saved.FolderId.Should().Be(move ? target.Id : source.Id);
        saved.Name.Should().Be(move ? workflow.Name : "Renamed");
        (await db.AuditLog.CountAsync(a => a.Action == AuditActions.DbAdminRowUpdated)).Should().Be(1);

        await notifier.StepCompletedAsync(execution.Id, workflow.Id, "step", null, ExecutionStatus.Succeeded, "after", null, DateTime.UtcNow);
        await notifier.FlushForTestAsync();
        proxy.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), move ? Times.Never() : Times.Once());
        groups.Verify(g => g.RemoveFromGroupAsync("local-reader", It.IsAny<string>(), It.IsAny<CancellationToken>()), move ? Times.Exactly(2) : Times.Never());
        if (move)
        {
            var rejoin = () => reader.JoinExecution(execution.Id.ToString());
            await rejoin.Should().ThrowAsync<HubException>();
        }
    }

    private static ClaimsPrincipal Principal(Guid id, UserRole role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Role, role.ToString())], "test"));
}
