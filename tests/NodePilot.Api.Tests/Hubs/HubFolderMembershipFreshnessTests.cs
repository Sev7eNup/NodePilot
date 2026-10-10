using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NodePilot.Api.Hubs;
using NodePilot.Api.Security;
using NodePilot.Api.Security.Oidc;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Hubs;

[Collection(ExecutionHubStaticStateCollection.Name)]
public sealed class HubFolderMembershipFreshnessTests : IDisposable
{
    public HubFolderMembershipFreshnessTests() => Clear();
    public void Dispose() => Clear();
    private static void Clear()
    {
        ExecutionHub.ClearAuthMapForTest();
        ExecutionHub.ClearGroupsForTest();
        ExecutionHub.ClearOpsFeedForTest();
    }

    [Theory]
    [InlineData(16, false, false, false)]
    [InlineData(4.8, false, false, false)]
    [InlineData(2, false, false, true)]
    [InlineData(16, true, false, true)]
    [InlineData(16, false, true, false)]
    public async Task Sweep_RevalidatesExistingFolderSubscriptionsWithoutLoggingOutValidIdentity(
        double folderAgeMinutes, bool directGrant, bool removeFails, bool expectedAccess)
    {
        await using var db = TestDbFactory.Create();
        const string authority = "https://idp.example.test/tenant";
        var user = new User
        {
            Id = Guid.NewGuid(), Username = "hub-user", Provider = AuthProvider.Oidc,
            Role = UserRole.Operator, IsActive = true,
        };
        var folder = new SharedWorkflowFolder
        {
            Id = Guid.NewGuid(), Name = "Private", Path = "/Private", Depth = 1,
            ParentFolderId = SharedWorkflowFolder.RootFolderId,
        };
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "Private workflow", FolderId = folder.Id };
        var execution = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = workflow.Id };
        db.AddRange(user, folder, workflow, execution,
            new ExternalIdentity { Id = Guid.NewGuid(), UserId = user.Id, Authority = authority, Subject = "subject" },
            new DirectoryMembership { UserId = user.Id, Authority = authority, GroupKey = "admission", LastSeenAt = DateTime.UtcNow },
            new DirectoryMembership
            {
                UserId = user.Id, Authority = authority, GroupKey = "folder",
                LastSeenAt = DateTime.UtcNow.AddMinutes(-folderAgeMinutes),
            },
            new SharedFolderPermission
            {
                Id = Guid.NewGuid(), FolderId = folder.Id, PrincipalType = FolderPrincipalType.Group,
                PrincipalAuthority = authority, PrincipalKey = "folder", Role = SharedFolderRole.FolderViewer,
            });
        if (directGrant) db.Add(new SharedFolderPermission
        {
            Id = Guid.NewGuid(), FolderId = folder.Id, PrincipalType = FolderPrincipalType.User,
            PrincipalKey = user.Id.ToString("D"), Role = SharedFolderRole.FolderViewer,
        });
        await db.SaveChangesAsync();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, nameof(UserRole.Operator))], "test"));
        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.User).Returns(principal);
        ExecutionHub.RegisterAuthForTest("connection", "valid-jti", user.Id, context.Object);
        ExecutionHub.RegisterGroupForTest("connection", $"workflow-{workflow.Id}");
        ExecutionHub.RegisterGroupForTest("connection", execution.Id.ToString());
        ExecutionHub.RegisterOpsFeedForTest("connection", false, [folder.Id]);

        var groupManager = new Mock<IGroupManager>();
        groupManager.Setup(g => g.RemoveFromGroupAsync("connection", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => removeFails ? Task.FromException(new IOException("transport failed")) : Task.CompletedTask);
        var hub = new Mock<IHubContext<ExecutionHub>>();
        hub.SetupGet(h => h.Groups).Returns(groupManager.Object);
        var services = new ServiceCollection().AddSingleton(db)
            .AddSingleton(Options.Create(new AuthenticationPolicyOptions { MaxAuthorizationStalenessMinutes = 5 }))
            .AddSingleton(Options.Create(new EnterpriseOidcOptions
            {
                Enabled = true, Authority = authority, AllowedGroupIds = ["admission"],
                GlobalRoleMappings = [new OidcRoleMapping { GroupId = "admission", Role = UserRole.Operator }],
            }))
            .AddScoped<ExternalAuthorizationEvaluator>();
        using var provider = services.BuildServiceProvider();
        var sweeper = new HubRevocationSweeper(provider.GetRequiredService<IServiceScopeFactory>(), hub.Object,
            NullLogger<HubRevocationSweeper>.Instance, new ConfigurationBuilder().Build(), TestDatabaseAvailability.Available);

        await sweeper.SweepOnceAsync(default);

        ExecutionHub.HasGroupSubscribers($"workflow-{workflow.Id}").Should().Be(expectedAccess);
        ExecutionHub.HasGroupSubscribers(execution.Id.ToString()).Should().Be(expectedAccess);
        ExecutionHub.GetOpsFeedConnections(folder.Id).Contains("connection").Should().Be(expectedAccess);
        context.Verify(c => c.Abort(), removeFails ? Times.Once() : Times.Never());
        groupManager.Verify(g => g.RemoveFromGroupAsync("connection", It.IsAny<string>(), It.IsAny<CancellationToken>()),
            expectedAccess ? Times.Never() : removeFails ? Times.Once() : Times.Exactly(2));
    }
}
