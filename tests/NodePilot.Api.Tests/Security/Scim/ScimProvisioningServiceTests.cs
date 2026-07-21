using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using NodePilot.Api.Security;
using NodePilot.Api.Security.Oidc;
using NodePilot.Api.Security.Scim;
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

    private sealed class ThrowingAuditStager : IAuditStager
    {
        public AuditLogEntry Build(
            string action,
            AuditActor actor,
            string? resourceType = null,
            Guid? resourceId = null,
            string? details = null) => throw new InvalidOperationException("audit staging failed");
    }
}
