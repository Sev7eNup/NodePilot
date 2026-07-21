using System.Security.Claims;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NodePilot.Api.Security.Oidc;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Security.Oidc;

public sealed class OidcIdentityMapperTests : IDisposable
{
    private const string Issuer = "https://idp.example.test/tenant";
    private const string AllowedGroup = "nodepilot-users";
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly NodePilotDbContext _db;

    public OidcIdentityMapperTests()
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
    public async Task AllowedPrincipal_IsProvisionedByIssuerAndSubject()
    {
        var mapper = Mapper();
        var principal = Principal("subject-1", "alice@example.test", AllowedGroup, "nodepilot-admins");

        var result = await mapper.MapAsync(principal, default);

        result.Succeeded.Should().BeTrue();
        result.User!.Provider.Should().Be(AuthProvider.Oidc);
        result.User.Role.Should().Be(UserRole.Admin);
        (await _db.ExternalIdentities.SingleAsync(i => i.UserId == result.User.Id))
            .Should().Match<ExternalIdentity>(i => i.Authority == Issuer && i.Subject == "subject-1");
        (await _db.DirectoryMemberships.Where(m => m.UserId == result.User.Id)
                .Select(m => m.GroupKey).ToListAsync())
            .Should().BeEquivalentTo(AllowedGroup, "nodepilot-admins");
    }

    [Fact]
    public async Task SameSubjectWithRenamedUsername_ReusesExistingUser()
    {
        var mapper = Mapper();
        var first = await mapper.MapAsync(Principal("subject-1", "alice@example.test", AllowedGroup), default);
        var second = await mapper.MapAsync(Principal("subject-1", "renamed@example.test", AllowedGroup), default);

        second.Succeeded.Should().BeTrue();
        second.User!.Id.Should().Be(first.User!.Id);
        (await _db.Users.CountAsync(u => u.Provider == AuthProvider.Oidc)).Should().Be(1);
    }

    [Fact]
    public async Task MissingAllowedGroup_IsDeniedWithoutCreatingUser()
    {
        var result = await Mapper().MapAsync(
            Principal("subject-1", "alice@example.test", "unrelated-group"), default);

        result.Failure.Should().Be(OidcMapFailure.AccessNotAssigned);
        (await _db.Users.AnyAsync(u => u.Provider == AuthProvider.Oidc)).Should().BeFalse();
    }

    [Fact]
    public async Task ScalarGroupClaim_WithSurroundingWhitespace_IsNotNormalizedIntoAccess()
    {
        var result = await Mapper().MapAsync(
            Principal("subject-1", "alice@example.test", $"{AllowedGroup} "), default);

        result.Failure.Should().Be(OidcMapFailure.AccessNotAssigned);
    }

    [Fact]
    public async Task TokenGroupOverage_UsesScimProvisionedMembershipSnapshot()
    {
        var user = new User
        {
            Id = Guid.NewGuid(), Username = "alice@example.test", Provider = AuthProvider.Oidc,
            ExternalId = "subject-1", Role = UserRole.Viewer, IsActive = true,
            LastDirectorySyncAt = DateTime.UtcNow, DirectorySyncStatus = "ScimCurrent",
        };
        _db.AddRange(user,
            new ExternalIdentity
            {
                Id = Guid.NewGuid(), UserId = user.Id, Authority = Issuer, Subject = "subject-1",
            },
            new DirectoryMembership
            {
                UserId = user.Id, Authority = Issuer, GroupKey = AllowedGroup,
                LastSeenAt = DateTime.UtcNow,
            });
        await _db.SaveChangesAsync();

        var principal = Principal("subject-1", "alice@example.test");
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("hasgroups", "true"));
        var result = await Mapper().MapAsync(principal, default);

        result.Succeeded.Should().BeTrue();
        result.User!.Id.Should().Be(user.Id);
    }

    [Fact]
    public async Task TokenGroupOverage_ReconcilesUserFreshnessToObservedMembershipTime()
    {
        var observedAt = DateTime.UtcNow.AddMinutes(-2);
        var user = new User
        {
            Id = Guid.NewGuid(), Username = "alice@example.test", Provider = AuthProvider.Oidc,
            ExternalId = "subject-1", Role = UserRole.Viewer, IsActive = true,
            LastDirectorySyncAt = DateTime.UtcNow.AddMinutes(-30), DirectorySyncStatus = "ScimCurrent",
        };
        _db.AddRange(user,
            new ExternalIdentity
            {
                Id = Guid.NewGuid(), UserId = user.Id, Authority = Issuer, Subject = "subject-1",
            },
            new DirectoryMembership
            {
                UserId = user.Id, Authority = Issuer, GroupKey = AllowedGroup,
                LastSeenAt = observedAt,
            });
        await _db.SaveChangesAsync();
        var principal = Principal("subject-1", "alice@example.test");
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("hasgroups", "true"));

        var result = await Mapper().MapAsync(principal, default);

        result.Succeeded.Should().BeTrue();
        _db.ChangeTracker.Clear();
        var persisted = await _db.Users.SingleAsync(x => x.Id == user.Id);
        persisted.LastDirectorySyncAt.Should().BeCloseTo(observedAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task MissingGroupsWithoutOverageSignal_DoesNotTrustStoredMemberships()
    {
        var user = new User
        {
            Id = Guid.NewGuid(), Username = "alice@example.test", Provider = AuthProvider.Oidc,
            ExternalId = "subject-1", Role = UserRole.Viewer, IsActive = true,
            LastDirectorySyncAt = DateTime.UtcNow, DirectorySyncStatus = "ScimCurrent",
        };
        _db.AddRange(user,
            new ExternalIdentity { Id = Guid.NewGuid(), UserId = user.Id, Authority = Issuer, Subject = "subject-1" },
            new DirectoryMembership { UserId = user.Id, Authority = Issuer, GroupKey = AllowedGroup, LastSeenAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        var result = await Mapper().MapAsync(Principal("subject-1", "alice@example.test"), default);

        result.Failure.Should().Be(OidcMapFailure.AccessNotAssigned);
        _db.ChangeTracker.Clear();
        (await _db.DirectoryMemberships.Where(membership => membership.UserId == user.Id)
                .Select(membership => membership.GroupKey).ToListAsync())
            .Should().Equal(AllowedGroup);
        (await _db.Users.SingleAsync(candidate => candidate.Id == user.Id))
            .SecurityStamp.Should().Be(0);
    }

    [Fact]
    public async Task OverageSignal_WithStaleMembershipSnapshot_IsDenied()
    {
        var user = new User
        {
            Id = Guid.NewGuid(), Username = "alice@example.test", Provider = AuthProvider.Oidc,
            ExternalId = "subject-1", Role = UserRole.Viewer, IsActive = true,
            LastDirectorySyncAt = DateTime.UtcNow.AddMinutes(-16), DirectorySyncStatus = "ScimCurrent",
        };
        _db.AddRange(user,
            new ExternalIdentity { Id = Guid.NewGuid(), UserId = user.Id, Authority = Issuer, Subject = "subject-1" },
            new DirectoryMembership { UserId = user.Id, Authority = Issuer, GroupKey = AllowedGroup, LastSeenAt = DateTime.UtcNow.AddMinutes(-16) });
        await _db.SaveChangesAsync();
        var principal = Principal("subject-1", "alice@example.test");
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("_claim_names", "{\"groups\":\"src1\"}"));

        var result = await Mapper().MapAsync(principal, default);

        result.Failure.Should().Be(OidcMapFailure.AccessNotAssigned);
    }

    [Fact]
    public async Task SubjectComparison_IsOrdinalAndRejectsCollationAlias()
    {
        var existing = new User
        {
            Id = Guid.NewGuid(), Username = "alice@example.test", Provider = AuthProvider.Oidc,
            ExternalId = "subject-a", Role = UserRole.Viewer, IsActive = true,
        };
        _db.AddRange(existing,
            new ExternalIdentity { Id = Guid.NewGuid(), UserId = existing.Id, Authority = Issuer, Subject = "subject-a" });
        await _db.SaveChangesAsync();

        var result = await Mapper().MapAsync(Principal("subject-A", "mallory@example.test", AllowedGroup), default);

        // SQLite's binary default does not return the differently-cased candidate, so this
        // is either a distinct safe JIT or a fail-closed collation conflict. It must never
        // resolve the existing user's identity.
        result.User?.Id.Should().NotBe(existing.Id);
    }

    [Fact]
    public async Task CompleteTokenLosingAccessGroup_PersistsSnapshotAndOffboardsImmediately()
    {
        var first = await Mapper().MapAsync(
            Principal("subject-offboard", "offboard@example.test", AllowedGroup), default);
        var userId = first.User!.Id;
        var session = new AuthSession
        {
            Id = Guid.NewGuid(), UserId = userId, AuthenticationMethod = "Oidc",
            CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1), CurrentJti = "old-session",
        };
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(), Name = "oidc-offboarding", DefinitionJson = "{}",
        };
        var executions = new[]
        {
            Execution(workflow, userId, ExecutionStatus.Pending),
            Execution(workflow, userId, ExecutionStatus.Running),
            Execution(workflow, userId, ExecutionStatus.Paused),
        };
        _db.AddRange(session, workflow);
        _db.AddRange(executions);
        await _db.SaveChangesAsync();

        var signalledAfterCommit = true;
        var signalIgnoredRequestAbort = true;
        using var request = new CancellationTokenSource();
        var engine = new Mock<IWorkflowEngine>();
        engine.Setup(candidate => candidate.CancelAsync(
                It.IsAny<Guid>(), "oidc-authorization-change", It.IsAny<CancellationToken>()))
            .Returns<Guid, string?, CancellationToken>(async (executionId, _, token) =>
            {
                // This callback is reached only after the database transaction committed.
                // Simulate the browser/proxy dropping the callback request at that exact
                // boundary; the security-critical engine token must remain independent.
                request.Cancel();
                signalIgnoredRequestAbort &= !token.IsCancellationRequested;
                var status = await _db.WorkflowExecutions.AsNoTracking()
                    .Where(candidate => candidate.Id == executionId)
                    .Select(candidate => candidate.Status)
                    .SingleAsync(token);
                signalledAfterCommit &= status == ExecutionStatus.Cancelled;
                return true;
            });

        var result = await Mapper(workflowEngine: engine.Object).MapAsync(
            Principal("subject-offboard", "offboard@example.test", "unrelated-group"),
            request.Token);

        result.Failure.Should().Be(OidcMapFailure.AccessNotAssigned);
        signalledAfterCommit.Should().BeTrue();
        request.IsCancellationRequested.Should().BeTrue();
        signalIgnoredRequestAbort.Should().BeTrue();
        _db.ChangeTracker.Clear();
        (await _db.DirectoryMemberships.Where(membership => membership.UserId == userId)
                .Select(membership => membership.GroupKey).ToListAsync())
            .Should().Equal("unrelated-group");
        (await _db.AuthSessions.SingleAsync(candidate => candidate.Id == session.Id))
            .RevokedAt.Should().NotBeNull();
        (await _db.WorkflowExecutions.Where(candidate => candidate.StartedByUserId == userId)
                .Select(candidate => candidate.Status).ToListAsync())
            .Should().OnlyContain(status => status == ExecutionStatus.Cancelled);
        (await _db.AuditLog.SingleAsync(entry =>
                entry.ResourceId == userId && entry.Action == AuditActions.UserDirectorySynced))
            .Details.Should().Contain("Oidc");
    }

    [Fact]
    public async Task LastOidcAdminDemotion_DeniesButStoresFreshGroupsAndOffboards()
    {
        var first = await Mapper().MapAsync(
            Principal("subject-admin", "admin@example.test", AllowedGroup, "nodepilot-admins"),
            default);
        var userId = first.User!.Id;
        var recovery = await _db.Users.SingleAsync(candidate => candidate.Username == "recovery-admin");
        recovery.Role = UserRole.Operator;
        var session = new AuthSession
        {
            Id = Guid.NewGuid(), UserId = userId, AuthenticationMethod = "Oidc",
            CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1), CurrentJti = "admin-session",
        };
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(), Name = "admin-demotion", DefinitionJson = "{}",
        };
        var execution = Execution(workflow, userId, ExecutionStatus.Running);
        _db.AddRange(session, workflow, execution);
        await _db.SaveChangesAsync();
        var originalStamp = first.User.SecurityStamp;

        var result = await Mapper().MapAsync(
            Principal("subject-admin", "admin@example.test", AllowedGroup), default);

        result.Failure.Should().Be(OidcMapFailure.LastAdmin);
        _db.ChangeTracker.Clear();
        var persisted = await _db.Users.SingleAsync(candidate => candidate.Id == userId);
        persisted.Role.Should().Be(UserRole.Admin, "the database must retain its last active Admin");
        persisted.SecurityStamp.Should().Be(originalStamp + 1);
        (await _db.DirectoryMemberships.Where(membership => membership.UserId == userId)
                .Select(membership => membership.GroupKey).ToListAsync())
            .Should().Equal(AllowedGroup);
        (await _db.AuthSessions.SingleAsync(candidate => candidate.Id == session.Id))
            .RevokedAt.Should().NotBeNull();
        (await _db.WorkflowExecutions.SingleAsync(candidate => candidate.Id == execution.Id))
            .Status.Should().Be(ExecutionStatus.Cancelled);
    }

    [Fact]
    public async Task ReconciliationFailure_RollsBackRoleGroupsSessionsExecutionsAndAudit()
    {
        var first = await Mapper().MapAsync(
            Principal("subject-rollback", "rollback@example.test", AllowedGroup, "nodepilot-admins"),
            default);
        var userId = first.User!.Id;
        var session = new AuthSession
        {
            Id = Guid.NewGuid(), UserId = userId, AuthenticationMethod = "Oidc",
            CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1), CurrentJti = "rollback-session",
        };
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(), Name = "rollback", DefinitionJson = "{}",
        };
        var execution = Execution(workflow, userId, ExecutionStatus.Running);
        _db.AddRange(session, workflow, execution);
        await _db.SaveChangesAsync();
        var auditCount = await _db.AuditLog.CountAsync();

        var act = () => Mapper(auditStager: new ThrowingAuditStager()).MapAsync(
            Principal("subject-rollback", "rollback@example.test", AllowedGroup), default);

        await act.Should().ThrowAsync<InjectedReconciliationException>();
        _db.ChangeTracker.Clear();
        var persisted = await _db.Users.SingleAsync(candidate => candidate.Id == userId);
        persisted.Role.Should().Be(UserRole.Admin);
        persisted.SecurityStamp.Should().Be(first.User.SecurityStamp);
        (await _db.DirectoryMemberships.Where(membership => membership.UserId == userId)
                .Select(membership => membership.GroupKey).ToListAsync())
            .Should().BeEquivalentTo(AllowedGroup, "nodepilot-admins");
        (await _db.AuthSessions.SingleAsync(candidate => candidate.Id == session.Id))
            .RevokedAt.Should().BeNull();
        (await _db.WorkflowExecutions.SingleAsync(candidate => candidate.Id == execution.Id))
            .Status.Should().Be(ExecutionStatus.Running);
        (await _db.AuditLog.CountAsync()).Should().Be(auditCount);
    }

    private OidcIdentityMapper Mapper(
        IWorkflowEngine? workflowEngine = null,
        IAuditStager? auditStager = null) => new(
        _db,
        Options.Create(new EnterpriseOidcOptions
        {
            Enabled = true,
            Authority = Issuer,
            ClientId = "nodepilot",
            ClientSecret = "test-secret",
            AllowedGroupIds = [AllowedGroup],
            GlobalRoleMappings =
            [
                new OidcRoleMapping { GroupId = "nodepilot-admins", Role = UserRole.Admin },
            ],
        }),
        NullLogger<OidcIdentityMapper>.Instance,
        workflowEngine: workflowEngine,
        auditStager: auditStager);

    private static WorkflowExecution Execution(
        Workflow workflow,
        Guid userId,
        ExecutionStatus status) => new()
    {
        Id = Guid.NewGuid(), Workflow = workflow, WorkflowId = workflow.Id,
        StartedByUserId = userId, Status = status, StartedAt = DateTime.UtcNow,
        TriggeredBy = "test",
    };

    private sealed class ThrowingAuditStager : IAuditStager
    {
        public AuditLogEntry Build(
            string action,
            AuditActor actor,
            string? resourceType = null,
            Guid? resourceId = null,
            string? details = null) => throw new InjectedReconciliationException();
    }

    private sealed class InjectedReconciliationException : Exception;

    private static ClaimsPrincipal Principal(string subject, string username, params string[] groups)
    {
        var claims = new List<Claim>
        {
            new("iss", Issuer),
            new("sub", subject),
            new("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
            new("preferred_username", username),
        };
        claims.AddRange(groups.Select(group => new Claim("groups", group)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "oidc"));
    }
}
