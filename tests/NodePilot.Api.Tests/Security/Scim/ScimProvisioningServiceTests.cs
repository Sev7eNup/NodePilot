using System.Security.Claims;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Api.Security;
using NodePilot.Api.Security.Oidc;
using NodePilot.Api.Security.Scim;
using NodePilot.Api.Tests.TestSupport;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Security.Scim;

public sealed class ScimProvisioningServiceTests : IDisposable
{
    private const string Authority = "https://idp.example.test/tenant";
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly NodePilotDbContext _db;

    public ScimProvisioningServiceTests()
    {
        (_connection, _db) = TestDbFactory.CreateWithConnection();
        _db.Users.Add(new User
        {
            Id = Guid.NewGuid(), Username = "recovery-admin", Provider = AuthProvider.Local,
            PasswordHash = "hash", Role = UserRole.Admin, IsActive = true, IsBreakGlass = true,
        });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task CreateUser_IsIdempotentByAuthorityAndExternalId()
    {
        var request = new ScimUserWriteRequest
        {
            ExternalId = "subject-1", UserName = "alice@example.test", Active = true,
        };

        var first = await Service().CreateUserAsync(request, "https://nodepilot/scim/v2", default);
        var second = await Service().CreateUserAsync(request, "https://nodepilot/scim/v2", default);

        first.Created.Should().BeTrue();
        second.Succeeded.Should().BeTrue();
        second.Value!.Id.Should().Be(first.Value!.Id);
        (await _db.Users.CountAsync(u => u.Provider == AuthProvider.Oidc)).Should().Be(1);
        (await _db.ExternalIdentities.CountAsync(i => i.Authority == Authority && i.Subject == "subject-1"))
            .Should().Be(1);
    }

    [Fact]
    public async Task GroupProvisioning_UpdatesRoleAndRevokesExistingSession()
    {
        var created = await Service().CreateUserAsync(new ScimUserWriteRequest
        {
            ExternalId = "subject-1", UserName = "alice@example.test", Active = true,
        }, "https://nodepilot/scim/v2", default);
        var userId = Guid.Parse(created.Value!.Id);
        var session = new AuthSession
        {
            Id = Guid.NewGuid(), UserId = userId, AuthenticationMethod = "Oidc",
            CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1), AuthorizationVersion = 0,
        };
        _db.AuthSessions.Add(session);
        await _db.SaveChangesAsync();

        var group = await Service().CreateGroupAsync(new ScimGroupWriteRequest
        {
            ExternalId = "nodepilot-admins",
            DisplayName = "NodePilot Administrators",
            Members = [new ScimMember { Value = userId.ToString() }],
        }, "https://nodepilot/scim/v2", default);

        group.Created.Should().BeTrue();
        _db.ChangeTracker.Clear();
        (await _db.Users.SingleAsync(u => u.Id == userId)).Role.Should().Be(UserRole.Admin);
        (await _db.DirectoryMemberships.SingleAsync(m => m.UserId == userId)).GroupKey
            .Should().Be("nodepilot-admins");
        (await _db.AuthSessions.SingleAsync(s => s.Id == session.Id)).RevokedAt.Should().NotBeNull();
    }

    [Theory]
    [InlineData("nodepilot-admins", false)]
    [InlineData("nodepilot-admins", true)]
    [InlineData("nodepilot-users", false)]
    [InlineData("nodepilot-users", true)]
    [InlineData("folder-readers", false)]
    [InlineData("folder-readers", true)]
    public async Task GroupRemoval_OlderOidcCallback_CannotRestoreMembership(string removedGroup, bool delete)
    {
        var oidc = new OidcIdentityMapper(_db, Options.Create(new EnterpriseOidcOptions
        {
            Authority = Authority,
            AllowedGroupIds = ["nodepilot-users"],
            GlobalRoleMappings = [new OidcRoleMapping { GroupId = "nodepilot-admins", Role = UserRole.Admin }],
        }), NullLogger<OidcIdentityMapper>.Instance);
        var issuedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("iss", Authority), new Claim("sub", "delayed-callback"),
            new Claim("preferred_username", "delayed@example.test"),
            new Claim("iat", issuedAt.ToUnixTimeSeconds().ToString()),
            new Claim("groups", "nodepilot-users"), new Claim("groups", "nodepilot-admins"),
            new Claim("groups", "folder-readers"),
        ], "oidc"));
        var original = await oidc.MapAsync(principal, default);
        original.Succeeded.Should().BeTrue();
        var userId = original.User!.Id;
        var group = await Service().CreateGroupAsync(new ScimGroupWriteRequest
        {
            ExternalId = removedGroup, DisplayName = removedGroup,
            Members = [new ScimMember { Value = userId.ToString() }],
        }, "https://nodepilot/scim/v2", default);
        group.Succeeded.Should().BeTrue();
        var groupId = Guid.Parse(group.Value!.Id);
        if (delete)
            (await Service().DeleteGroupAsync(groupId, default)).Succeeded.Should().BeTrue();
        else
            (await Service().ReplaceGroupAsync(groupId, new ScimGroupWriteRequest
            {
                DisplayName = removedGroup, Members = [],
            }, "https://nodepilot/scim/v2", default)).Succeeded.Should().BeTrue();
        _db.ChangeTracker.Clear();
        var roleAfterRemoval = (await _db.Users.SingleAsync(user => user.Id == userId)).Role;
        var observation = (await _db.ExternalIdentities.SingleAsync(identity => identity.UserId == userId)).GroupsObservedAt;

        var delayed = await oidc.MapAsync(principal, default);

        delayed.Succeeded.Should().BeFalse();
        delayed.AuditReason.Should().Be("oidc_group_snapshot_superseded");
        (await _db.DirectoryMemberships.AnyAsync(membership => membership.UserId == userId
            && membership.GroupKey == removedGroup)).Should().BeFalse();
        (await _db.Users.SingleAsync(user => user.Id == userId)).Role.Should().Be(roleAfterRemoval);
        (await _db.ExternalIdentities.SingleAsync(identity => identity.UserId == userId)).GroupsObservedAt
            .Should().Be(observation);

        var claims = (ClaimsIdentity)principal.Identity!;
        claims.RemoveClaim(claims.FindFirst("iat")!);
        claims.RemoveClaim(claims.FindAll("groups").Single(claim => claim.Value == removedGroup));
        claims.AddClaim(new Claim("iat", DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeSeconds().ToString()));
        var fresh = await oidc.MapAsync(principal, default);
        fresh.AuditReason.Should().NotBe("oidc_group_snapshot_superseded");
        fresh.Succeeded.Should().Be(removedGroup != "nodepilot-users");
    }

    [Fact]
    public async Task DeleteUser_IsIdempotentAndLeavesTombstone()
    {
        var created = await Service().CreateUserAsync(new ScimUserWriteRequest
        {
            ExternalId = "subject-1", UserName = "alice@example.test", Active = true,
        }, "https://nodepilot/scim/v2", default);
        var userId = Guid.Parse(created.Value!.Id);

        (await Service().DeleteUserAsync(userId, default)).Succeeded.Should().BeTrue();
        (await Service().DeleteUserAsync(userId, default)).Succeeded.Should().BeTrue();

        _db.ChangeTracker.Clear();
        var user = await _db.Users.SingleAsync(u => u.Id == userId);
        user.IsActive.Should().BeFalse();
        user.IsTombstoned.Should().BeTrue();
        user.DirectorySyncStatus.Should().Be("ScimTombstoned");
    }

    [Fact]
    public async Task DeleteUser_AuditFailure_RollsBackAndNeverSignalsEngine()
    {
        var created = await Service().CreateUserAsync(new ScimUserWriteRequest
        {
            ExternalId = "subject-rollback", UserName = "rollback@example.test", Active = true,
        }, "https://nodepilot/scim/v2", default);
        var userId = Guid.Parse(created.Value!.Id);
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(), Name = "scim rollback", DefinitionJson = "{}",
        };
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(), Workflow = workflow, WorkflowId = workflow.Id,
            StartedByUserId = userId, Status = ExecutionStatus.Running,
            StartedAt = DateTime.UtcNow, TriggeredBy = "scheduleTrigger",
        };
        _db.AddRange(workflow, execution);
        await _db.SaveChangesAsync();
        var engine = new Mock<IWorkflowEngine>();

        var delete = () => Service(new ThrowingAuditStager(), engine.Object)
            .DeleteUserAsync(userId, CancellationToken.None);

        await delete.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("audit staging failed");
        _db.ChangeTracker.Clear();
        (await _db.Users.SingleAsync(candidate => candidate.Id == userId)).IsActive
            .Should().BeTrue();
        (await _db.WorkflowExecutions.SingleAsync(candidate => candidate.Id == execution.Id)).Status
            .Should().Be(ExecutionStatus.Running);
        engine.Verify(candidate => candidate.CancelAsync(
            It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RetainedGroupMember_RefreshesMembershipAndUserAuthorizationSnapshot()
    {
        var created = await Service().CreateUserAsync(new ScimUserWriteRequest
        {
            ExternalId = "subject-1", UserName = "alice@example.test", Active = true,
        }, "https://nodepilot/scim/v2", default);
        var userId = Guid.Parse(created.Value!.Id);
        var group = await Service().CreateGroupAsync(new ScimGroupWriteRequest
        {
            ExternalId = "nodepilot-users", DisplayName = "NodePilot Users",
            Members = [new ScimMember { Value = userId.ToString() }],
        }, "https://nodepilot/scim/v2", default);

        var stale = DateTime.UtcNow.AddMinutes(-30);
        var user = await _db.Users.SingleAsync(x => x.Id == userId);
        var membership = await _db.DirectoryMemberships.SingleAsync(x => x.UserId == userId);
        user.LastDirectorySyncAt = stale;
        membership.LastSeenAt = stale;
        await _db.SaveChangesAsync();

        var replaced = await Service().ReplaceGroupAsync(
            Guid.Parse(group.Value!.Id),
            new ScimGroupWriteRequest
            {
                ExternalId = "nodepilot-users", DisplayName = "NodePilot Users",
                Members = [new ScimMember { Value = userId.ToString() }],
            },
            "https://nodepilot/scim/v2",
            default);

        replaced.Succeeded.Should().BeTrue();
        _db.ChangeTracker.Clear();
        (await _db.Users.SingleAsync(x => x.Id == userId)).LastDirectorySyncAt
            .Should().BeAfter(stale.AddMinutes(20));
        (await _db.DirectoryMemberships.SingleAsync(x => x.UserId == userId)).LastSeenAt
            .Should().BeAfter(stale.AddMinutes(20));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FolderOnlyGroupRemoval_InvalidatesSessionAndCancelsExecution(bool deleteGroup)
    {
        var ct = TestContext.Current.CancellationToken;
        var created = await Service().CreateUserAsync(new ScimUserWriteRequest
        {
            ExternalId = "folder-member", UserName = "folder-member@example.test", Active = true,
        }, "https://nodepilot/scim/v2", ct);
        var userId = Guid.Parse(created.Value!.Id);
        await Service().CreateGroupAsync(new ScimGroupWriteRequest
        {
            ExternalId = "nodepilot-users", DisplayName = "Admission",
            Members = [new ScimMember { Value = userId.ToString() }],
        }, "https://nodepilot/scim/v2", ct);
        var folderGroup = await Service().CreateGroupAsync(new ScimGroupWriteRequest
        {
            ExternalId = "finance-readers", DisplayName = "Finance readers",
            Members = [new ScimMember { Value = userId.ToString() }],
        }, "https://nodepilot/scim/v2", ct);
        var folder = new SharedWorkflowFolder
        {
            Id = Guid.NewGuid(), Name = "Finance", Path = "/Finance", Depth = 1,
            ParentFolderId = SharedWorkflowFolder.RootFolderId,
        };
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "Private finance", FolderId = folder.Id, DefinitionJson = "{}" };
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(), Workflow = workflow, WorkflowId = workflow.Id,
            StartedByUserId = userId, Status = ExecutionStatus.Running, StartedAt = DateTime.UtcNow,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(), UserId = userId, AuthenticationMethod = "Oidc",
            CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddHours(1),
        };
        _db.AddRange(folder, workflow, execution, session, new SharedFolderPermission
        {
            Id = Guid.NewGuid(), FolderId = folder.Id, PrincipalType = FolderPrincipalType.Group,
            PrincipalAuthority = Authority, PrincipalKey = "finance-readers", Role = SharedFolderRole.FolderViewer,
        });
        await _db.SaveChangesAsync(ct);
        var oldStamp = (await _db.Users.SingleAsync(x => x.Id == userId, ct)).SecurityStamp;
        var engine = new Mock<IWorkflowEngine>();
        engine.Setup(x => x.CancelAsync(execution.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var service = Service(workflowEngine: engine.Object);
        if (deleteGroup)
            (await service.DeleteGroupAsync(Guid.Parse(folderGroup.Value!.Id), ct)).Succeeded.Should().BeTrue();
        else
            (await service.ReplaceGroupAsync(Guid.Parse(folderGroup.Value!.Id), new ScimGroupWriteRequest
            {
                DisplayName = "Finance readers", Members = [],
            }, "https://nodepilot/scim/v2", ct)).Succeeded.Should().BeTrue();

        _db.ChangeTracker.Clear();
        (await _db.Users.SingleAsync(x => x.Id == userId, ct)).SecurityStamp.Should().BeGreaterThan(oldStamp);
        (await _db.AuthSessions.SingleAsync(x => x.Id == session.Id, ct)).RevokedAt.Should().NotBeNull();
        (await _db.WorkflowExecutions.SingleAsync(x => x.Id == execution.Id, ct)).Status.Should().Be(ExecutionStatus.Cancelled);
        engine.Verify(x => x.CancelAsync(execution.Id, "scim-authorization-change", It.IsAny<CancellationToken>()), Times.Once);
    }

    private async Task<(Guid UserId, Guid ExecutionId, Guid SessionId)> SeedMemberWithRunningWorkAsync(
        string subject, CancellationToken ct)
    {
        var created = await Service().CreateUserAsync(new ScimUserWriteRequest
        {
            ExternalId = subject, UserName = $"{subject}@example.test", Active = true,
        }, "https://nodepilot/scim/v2", ct);
        var userId = Guid.Parse(created.Value!.Id);
        await Service().CreateGroupAsync(new ScimGroupWriteRequest
        {
            ExternalId = "nodepilot-users", DisplayName = "NodePilot Users",
            Members = [new ScimMember { Value = userId.ToString() }],
        }, "https://nodepilot/scim/v2", ct);
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = $"wf-{subject}", DefinitionJson = "{}" };
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(), Workflow = workflow, WorkflowId = workflow.Id,
            StartedByUserId = userId, Status = ExecutionStatus.Running, StartedAt = DateTime.UtcNow,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(), UserId = userId, AuthenticationMethod = "Oidc",
            CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddHours(1),
        };
        _db.AddRange(workflow, execution, session);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return (userId, execution.Id, session.Id);
    }

    private static Mock<IWorkflowEngine> EngineAccepting(Guid executionId)
    {
        var engine = new Mock<IWorkflowEngine>();
        engine.Setup(x => x.CancelAsync(executionId, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return engine;
    }

    [Fact]
    public async Task AddingAdminGroup_RevokesSessionButKeepsRunningExecution()
    {
        var ct = TestContext.Current.CancellationToken;
        var (userId, executionId, sessionId) = await SeedMemberWithRunningWorkAsync("promoted", ct);
        var engine = EngineAccepting(executionId);

        var result = await Service(workflowEngine: engine.Object).CreateGroupAsync(new ScimGroupWriteRequest
        {
            ExternalId = "nodepilot-admins", DisplayName = "NodePilot Administrators",
            Members = [new ScimMember { Value = userId.ToString() }],
        }, "https://nodepilot/scim/v2", ct);

        result.Succeeded.Should().BeTrue();
        _db.ChangeTracker.Clear();
        (await _db.Users.SingleAsync(x => x.Id == userId, ct)).Role.Should().Be(UserRole.Admin);
        (await _db.AuthSessions.SingleAsync(x => x.Id == sessionId, ct)).RevokedAt.Should().NotBeNull();
        (await _db.WorkflowExecutions.SingleAsync(x => x.Id == executionId, ct)).Status.Should().Be(ExecutionStatus.Running);
        engine.Verify(x => x.CancelAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemovingAdminGroup_DemotesAndCancelsRunningExecution()
    {
        var ct = TestContext.Current.CancellationToken;
        var (userId, executionId, sessionId) = await SeedMemberWithRunningWorkAsync("demoted", ct);
        var admins = await Service().CreateGroupAsync(new ScimGroupWriteRequest
        {
            ExternalId = "nodepilot-admins", DisplayName = "NodePilot Administrators",
            Members = [new ScimMember { Value = userId.ToString() }],
        }, "https://nodepilot/scim/v2", ct);
        var engine = EngineAccepting(executionId);

        var result = await Service(workflowEngine: engine.Object).ReplaceGroupAsync(
            Guid.Parse(admins.Value!.Id),
            new ScimGroupWriteRequest { DisplayName = "NodePilot Administrators", Members = [] },
            "https://nodepilot/scim/v2", ct);

        result.Succeeded.Should().BeTrue();
        _db.ChangeTracker.Clear();
        (await _db.Users.SingleAsync(x => x.Id == userId, ct)).Role.Should().Be(UserRole.Viewer);
        (await _db.AuthSessions.SingleAsync(x => x.Id == sessionId, ct)).RevokedAt.Should().NotBeNull();
        (await _db.WorkflowExecutions.SingleAsync(x => x.Id == executionId, ct)).Status.Should().Be(ExecutionStatus.Cancelled);
        engine.Verify(x => x.CancelAsync(executionId, "scim-authorization-change", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RenamingUser_RevokesSessionButKeepsRunningExecution()
    {
        var ct = TestContext.Current.CancellationToken;
        var (userId, executionId, sessionId) = await SeedMemberWithRunningWorkAsync("renamed", ct);
        var engine = EngineAccepting(executionId);

        var result = await Service(workflowEngine: engine.Object).ReplaceUserAsync(userId,
            new ScimUserWriteRequest { UserName = "renamed-new@example.test", Active = true },
            "https://nodepilot/scim/v2", ct);

        result.Succeeded.Should().BeTrue();
        _db.ChangeTracker.Clear();
        (await _db.Users.SingleAsync(x => x.Id == userId, ct)).Username.Should().Be("renamed-new@example.test");
        (await _db.AuthSessions.SingleAsync(x => x.Id == sessionId, ct)).RevokedAt.Should().NotBeNull();
        (await _db.WorkflowExecutions.SingleAsync(x => x.Id == executionId, ct)).Status.Should().Be(ExecutionStatus.Running);
        engine.Verify(x => x.CancelAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeactivatingUser_CancelsRunningExecution()
    {
        var ct = TestContext.Current.CancellationToken;
        var (userId, executionId, _) = await SeedMemberWithRunningWorkAsync("deactivated", ct);
        var engine = EngineAccepting(executionId);

        var result = await Service(workflowEngine: engine.Object).ReplaceUserAsync(userId,
            new ScimUserWriteRequest { UserName = "deactivated@example.test", Active = false },
            "https://nodepilot/scim/v2", ct);

        result.Succeeded.Should().BeTrue();
        _db.ChangeTracker.Clear();
        (await _db.WorkflowExecutions.SingleAsync(x => x.Id == executionId, ct)).Status.Should().Be(ExecutionStatus.Cancelled);
        engine.Verify(x => x.CancelAsync(executionId, "scim-authorization-change", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteUser_CancelsRunningExecution()
    {
        var ct = TestContext.Current.CancellationToken;
        var (userId, executionId, _) = await SeedMemberWithRunningWorkAsync("deprovisioned", ct);
        var engine = EngineAccepting(executionId);

        (await Service(workflowEngine: engine.Object).DeleteUserAsync(userId, ct)).Succeeded.Should().BeTrue();

        _db.ChangeTracker.Clear();
        (await _db.WorkflowExecutions.SingleAsync(x => x.Id == executionId, ct)).Status.Should().Be(ExecutionStatus.Cancelled);
        engine.Verify(x => x.CancelAsync(executionId, "scim-authorization-change", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteUser_DeactivatedAdminWithActiveBreakGlass_Succeeds()
    {
        var userId = await SeedScimAdminAsync("former-admin", isActive: false);

        var result = await Service().DeleteUserAsync(userId, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue("an already deactivated admin is not one of the active admins the lock protects");
        _db.ChangeTracker.Clear();
        (await _db.Users.SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken))
            .IsTombstoned.Should().BeTrue();
    }

    [Fact]
    public async Task PatchUser_RepeatedDeactivationOfInactiveAdmin_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = await SeedScimAdminAsync("former-admin", isActive: false);
        var deactivate = new ScimPatchRequest
        {
            Schemas = [ScimSchemas.Patch],
            Operations =
            [
                new ScimPatchOperation
                {
                    Op = "replace", Path = "active",
                    Value = System.Text.Json.JsonDocument.Parse("false").RootElement.Clone(),
                },
            ],
        };

        var first = await Service().PatchUserAsync(userId, deactivate, "https://nodepilot/scim/v2", ct);
        var second = await Service().PatchUserAsync(userId, deactivate, "https://nodepilot/scim/v2", ct);

        first.Succeeded.Should().BeTrue();
        second.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteUser_LastActiveAdmin_Returns409()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = await SeedScimAdminAsync("only-admin", isActive: true);
        var breakGlass = await _db.Users.SingleAsync(u => u.Username == "recovery-admin", ct);
        breakGlass.IsActive = false;
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();

        var result = await Service().DeleteUserAsync(userId, ct);

        result.StatusCode.Should().Be(409);
        _db.ChangeTracker.Clear();
        (await _db.Users.SingleAsync(u => u.Id == userId, ct)).IsActive.Should().BeTrue();
    }

    private async Task<Guid> SeedScimAdminAsync(string subject, bool isActive)
    {
        var created = await Service().CreateUserAsync(new ScimUserWriteRequest
        {
            ExternalId = subject, UserName = $"{subject}@example.test", Active = true,
        }, "https://nodepilot/scim/v2", TestContext.Current.CancellationToken);
        var userId = Guid.Parse(created.Value!.Id);
        var user = await _db.Users.SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
        user.Role = UserRole.Admin;
        user.IsActive = isActive;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _db.ChangeTracker.Clear();
        return userId;
    }

    private ScimProvisioningService Service(
        IAuditStager? auditStager = null,
        IWorkflowEngine? workflowEngine = null) => new(
        _db,
        Options.Create(new ScimOptions
        {
            Enabled = true,
            BearerToken = new string('s', 32),
            Authority = Authority,
        }),
        Options.Create(new EnterpriseOidcOptions
        {
            Enabled = true,
            Authority = Authority,
            AllowedGroupIds = ["nodepilot-users"],
            GlobalRoleMappings =
            [
                new OidcRoleMapping { GroupId = "nodepilot-admins", Role = UserRole.Admin },
            ],
        }),
        Options.Create(new AuthenticationPolicyOptions
        {
            MaxAuthorizationStalenessMinutes = 15,
        }),
        auditStager ?? new AuditStager(),
        workflowEngine: workflowEngine);

}
