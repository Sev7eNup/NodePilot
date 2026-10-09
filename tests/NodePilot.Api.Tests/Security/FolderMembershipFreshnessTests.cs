using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodePilot.Api.Security;
using NodePilot.Api.Security.Oidc;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Security;

public sealed class FolderMembershipFreshnessTests
{
    [Theory]
    [InlineData(16, 15, false)]
    [InlineData(10, 5, false)]
    [InlineData(2, 5, true)]
    [InlineData(10, 15, true)]
    public async Task FolderOnlyMembership_ExpiresIndependentlyOfFreshAdmission(
        int folderAgeMinutes, int budgetMinutes, bool expectedAccess)
    {
        await using var db = TestDbFactory.Create();
        const string authority = "https://idp.example.test/tenant";
        var user = new User
        {
            Id = Guid.NewGuid(), Username = "folder-operator", Provider = AuthProvider.Oidc,
            Role = UserRole.Operator, IsActive = true, LastDirectorySyncAt = DateTime.UtcNow,
        };
        var childFolder = new SharedWorkflowFolder
        {
            Id = Guid.NewGuid(), Name = "Restricted", Path = "/Restricted", Depth = 1,
            ParentFolderId = SharedWorkflowFolder.RootFolderId,
        };
        var parent = new Workflow
        {
            Id = Guid.NewGuid(), Name = "Parent", FolderId = SharedWorkflowFolder.RootFolderId,
            PublishedByUserId = user.Id,
        };
        var child = new Workflow { Id = Guid.NewGuid(), Name = "Child", FolderId = childFolder.Id };
        db.AddRange(user, childFolder, parent, child,
            new ExternalIdentity { Id = Guid.NewGuid(), UserId = user.Id, Authority = authority, Subject = "subject" },
            new DirectoryMembership
            {
                UserId = user.Id, Authority = authority, GroupKey = "admission-operators", LastSeenAt = DateTime.UtcNow,
            },
            new DirectoryMembership
            {
                UserId = user.Id, Authority = authority, GroupKey = "restricted-folder",
                LastSeenAt = DateTime.UtcNow.AddMinutes(-folderAgeMinutes),
            },
            new SharedFolderPermission
            {
                Id = Guid.NewGuid(), FolderId = childFolder.Id, PrincipalType = FolderPrincipalType.Group,
                PrincipalAuthority = authority, PrincipalKey = "restricted-folder", Role = SharedFolderRole.FolderOperator,
            });
        await db.SaveChangesAsync();
        var policy = Options.Create(new AuthenticationPolicyOptions { MaxAuthorizationStalenessMinutes = budgetMinutes });
        var evaluator = new ExternalAuthorizationEvaluator(db, policy, Options.Create(new EnterpriseOidcOptions
        {
            Enabled = true, Authority = authority, AllowedGroupIds = ["admission-operators"],
            GlobalRoleMappings = [new OidcRoleMapping { GroupId = "admission-operators", Role = UserRole.Operator }],
        }));
        (await evaluator.EvaluateAsync(user, DateTime.UtcNow, default)).IsCurrent.Should().BeTrue(
            "SCIM can refresh admission independently of a folder-only group's observation");

        using var services = new ServiceCollection().AddSingleton(db).AddSingleton(policy).BuildServiceProvider();
        var authorization = ActivatorUtilities.CreateInstance<ResourceAuthorizationService>(services);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, nameof(UserRole.Operator))], "test"));
        (await authorization.CanAccessWorkflowAsync(principal, childFolder.Id, ResourceOp.Read)).Should().Be(expectedAccess);
        (await authorization.CanAccessWorkflowAsync(principal, childFolder.Id, ResourceOp.Run)).Should().Be(expectedAccess);
        (await authorization.GetAccessibleFolderIdsAsync(principal)).FolderIds.Contains(childFolder.Id).Should().Be(expectedAccess);

        var resolver = new SubWorkflowAuthorizationResolver(db, policy, evaluator);
        var blocked = await resolver.IsBlockedAsync(new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = parent.Id, StartedByUserId = user.Id,
        }, child, default);
        (blocked is null).Should().Be(expectedAccess, "background sub-workflows must use the same fresh folder grants");
    }
}
