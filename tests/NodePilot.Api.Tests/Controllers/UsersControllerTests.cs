using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public class UsersControllerTests
{
    private static (UsersController controller, CapturingAuditWriter audit) NewController(
        NodePilot.Data.NodePilotDbContext db, Guid? callerId = null)
    {
        var audit = new CapturingAuditWriter();
        var controller = new UsersController(db, audit, new MemoryCache(new MemoryCacheOptions()));
        var id = callerId ?? Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.Role, "Admin"),
                new Claim(ClaimTypes.NameIdentifier, id.ToString())
            }, "TestAuth"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return (controller, audit);
    }

    private static UsersController Controller(NodePilot.Data.NodePilotDbContext db, Guid? callerId = null)
        => NewController(db, callerId).controller;

    private static User AdminUser(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Username = "admin",
        PasswordHash = BCrypt.Net.BCrypt.HashPassword("Admin@123", 4),
        Role = UserRole.Admin,
        IsActive = true,
        IsBreakGlass = true,
        CreatedAt = DateTime.UtcNow,
        PasswordChangedAt = DateTime.UtcNow
    };

    private static User OperatorUser(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Username = "operator",
        PasswordHash = "hash",
        Role = UserRole.Operator,
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        PasswordChangedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task GetAll_ReturnsAllUsers_OrderedByUsername()
    {
        var db = TestDbFactory.Create();
        db.Users.AddRange(
            new User { Id = Guid.NewGuid(), Username = "zed", PasswordHash = "h", Role = UserRole.Viewer, IsActive = true, CreatedAt = DateTime.UtcNow, PasswordChangedAt = DateTime.UtcNow },
            new User { Id = Guid.NewGuid(), Username = "alice", PasswordHash = "h", Role = UserRole.Admin, IsActive = true, CreatedAt = DateTime.UtcNow, PasswordChangedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var result = await Controller(db).GetAll(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var users = ok.Value.Should().BeAssignableTo<List<UserResponse>>().Subject;
        users.Should().HaveCount(2);
        users[0].Username.Should().Be("alice");
        users[1].Username.Should().Be("zed");
    }

    [Fact]
    public async Task GetAll_DoesNotExposePasswordHash()
    {
        var db = TestDbFactory.Create();
        db.Users.Add(AdminUser());
        await db.SaveChangesAsync();

        var result = await Controller(db).GetAll(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var users = ok.Value.Should().BeAssignableTo<List<UserResponse>>().Subject;
        // UserResponse record has no PasswordHash field — verify via JSON serialization
        var json = System.Text.Json.JsonSerializer.Serialize(users);
        json.Should().NotContain("PasswordHash").And.NotContain("passwordHash");
    }

    [Fact]
    public async Task Create_ValidUser_ReturnsCreated()
    {
        var db = TestDbFactory.Create();
        var controller = Controller(db);

        var result = await controller.Create(
            new CreateUserRequest("newuser", "Str0ng@Pass!", "Operator"),
            CancellationToken.None);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        db.Users.Should().ContainSingle(u => u.Username == "newuser");
    }

    [Fact]
    public async Task Create_DuplicateUsername_Returns409()
    {
        var db = TestDbFactory.Create();
        db.Users.Add(OperatorUser());
        await db.SaveChangesAsync();

        var result = await Controller(db).Create(
            new CreateUserRequest("operator", "Str0ng@Pass!", "Operator"),
            CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Create_WeakPassword_Returns400()
    {
        var db = TestDbFactory.Create();
        var result = await Controller(db).Create(
            new CreateUserRequest("newuser", "weak", "Operator"),
            CancellationToken.None);
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Update_ChangeRole_UpdatesInDb()
    {
        var db = TestDbFactory.Create();
        var adminId = Guid.NewGuid();
        var operatorId = Guid.NewGuid();
        db.Users.AddRange(AdminUser(adminId), OperatorUser(operatorId));
        await db.SaveChangesAsync();

        var result = await Controller(db).Update(operatorId,
            new UpdateUserRequest("Viewer", null, null),
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        db.Users.Find(operatorId)!.Role.Should().Be(UserRole.Viewer);
    }

    [Fact]
    public async Task Update_LastAdminDemotion_Returns400()
    {
        var db = TestDbFactory.Create();
        var adminId = Guid.NewGuid();
        db.Users.Add(AdminUser(adminId));
        await db.SaveChangesAsync();

        var result = await Controller(db).Update(adminId,
            new UpdateUserRequest("Operator", null, null),
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        db.Users.Find(adminId)!.Role.Should().Be(UserRole.Admin);
    }

    [Fact]
    public async Task Update_LastAdminDeactivation_Returns400()
    {
        var db = TestDbFactory.Create();
        var adminId = Guid.NewGuid();
        db.Users.Add(AdminUser(adminId));
        await db.SaveChangesAsync();

        var result = await Controller(db).Update(adminId,
            new UpdateUserRequest(null, false, null),
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        db.Users.Find(adminId)!.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Update_DeactivateNonLastAdmin_Succeeds()
    {
        var db = TestDbFactory.Create();
        var admin1Id = Guid.NewGuid();
        var admin2Id = Guid.NewGuid();
        db.Users.AddRange(AdminUser(admin1Id), new User
        {
            Id = admin2Id, Username = "admin2", PasswordHash = "h",
            Role = UserRole.Admin, IsActive = true,
            CreatedAt = DateTime.UtcNow, PasswordChangedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var result = await Controller(db).Update(admin2Id,
            new UpdateUserRequest(null, false, null),
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        db.Users.Find(admin2Id)!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Update_Deactivation_RevokesSessionsAndCancelsPendingExecutions()
    {
        var db = TestDbFactory.Create();
        var admin = AdminUser();
        var user = OperatorUser();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "scheduled", DefinitionJson = "{}" };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(), UserId = user.Id, AuthenticationMethod = "Local",
            CurrentJti = Guid.NewGuid().ToString("N"), CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddHours(1),
        };
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, TriggeredBy = "scheduleTrigger",
            StartedByUserId = user.Id, Status = ExecutionStatus.Pending,
            StartedAt = DateTime.UtcNow,
        };
        db.AddRange(admin, user, workflow, session, execution);
        await db.SaveChangesAsync();

        var result = await Controller(db).Update(user.Id,
            new UpdateUserRequest(null, false, null), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        db.ChangeTracker.Clear();
        (await db.AuthSessions.SingleAsync(x => x.Id == session.Id)).RevokedAt.Should().NotBeNull();
        (await db.WorkflowExecutions.SingleAsync(x => x.Id == execution.Id)).Should()
            .Match<WorkflowExecution>(x => x.Status == ExecutionStatus.Cancelled
                                           && x.CancelledBy == "admin-authorization-change");
    }

    [Fact]
    public async Task Delete_RegularUser_CreatesTombstone()
    {
        var db = TestDbFactory.Create();
        var adminId = Guid.NewGuid();
        var operatorId = Guid.NewGuid();
        db.Users.AddRange(AdminUser(adminId), OperatorUser(operatorId));
        await db.SaveChangesAsync();

        var result = await Controller(db, callerId: adminId).Delete(operatorId, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        var tombstone = db.Users.Find(operatorId);
        tombstone.Should().NotBeNull();
        tombstone!.IsActive.Should().BeFalse();
        tombstone.IsTombstoned.Should().BeTrue();
    }

    [Fact]
    public async Task Reactivate_TombstonedUser_RestoresExplicitly()
    {
        var db = TestDbFactory.Create();
        var adminId = Guid.NewGuid();
        var operatorUser = OperatorUser(Guid.NewGuid());
        operatorUser.IsActive = false;
        operatorUser.IsTombstoned = true;
        db.Users.AddRange(AdminUser(adminId), operatorUser);
        await db.SaveChangesAsync();

        var result = await Controller(db, callerId: adminId)
            .Reactivate(operatorUser.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        db.Entry(operatorUser).Reload();
        operatorUser.IsActive.Should().BeTrue();
        operatorUser.IsTombstoned.Should().BeFalse();
    }

    [Fact]
    public async Task Reactivate_ExternalUser_InvalidatesOldAuthorizationSnapshotBeforeActivation()
    {
        var db = TestDbFactory.Create();
        var adminId = Guid.NewGuid();
        var external = new User
        {
            Id = Guid.NewGuid(), Username = "alice@example.test", Provider = AuthProvider.Ldap,
            ExternalId = "S-1-5-21-1-2-3-1001", Role = UserRole.Operator,
            IsActive = false, IsTombstoned = true, SecurityStamp = 4,
            LastDirectorySyncAt = DateTime.UtcNow,
            DirectorySyncStatus = "Tombstoned",
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(), UserId = external.Id, AuthenticationMethod = "Ldap",
            CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1), CurrentJti = Guid.NewGuid().ToString("N"),
        };
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "stale automation", DefinitionJson = "{}" };
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, StartedByUserId = external.Id,
            Status = ExecutionStatus.Pending, StartedAt = DateTime.UtcNow,
        };
        db.AddRange(
            AdminUser(adminId),
            external,
            new DirectoryMembership
            {
                UserId = external.Id,
                Authority = ExternalIdentity.ActiveDirectoryAuthority,
                GroupKey = "S-1-5-21-1-2-3-2001",
                LastSeenAt = DateTime.UtcNow,
            },
            session,
            workflow,
            execution);
        await db.SaveChangesAsync();

        var result = await Controller(db, callerId: adminId)
            .Reactivate(external.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        db.ChangeTracker.Clear();
        var reactivated = await db.Users.SingleAsync(candidate => candidate.Id == external.Id);
        reactivated.IsActive.Should().BeTrue();
        reactivated.IsTombstoned.Should().BeFalse();
        reactivated.SecurityStamp.Should().Be(5);
        reactivated.LastDirectorySyncAt.Should().BeNull();
        reactivated.DirectorySyncStatus.Should().Be("ReactivationReauthRequired");
        (await db.DirectoryMemberships.CountAsync(candidate => candidate.UserId == external.Id))
            .Should().Be(0);
        (await db.AuthSessions.SingleAsync(candidate => candidate.Id == session.Id)).RevokedAt
            .Should().NotBeNull();
        (await db.WorkflowExecutions.SingleAsync(candidate => candidate.Id == execution.Id)).Status
            .Should().Be(ExecutionStatus.Cancelled);
    }

    [Fact]
    public async Task Delete_LastAdmin_Returns400()
    {
        var db = TestDbFactory.Create();
        var adminId = Guid.NewGuid();
        db.Users.Add(AdminUser(adminId));
        await db.SaveChangesAsync();

        // caller is a different user (so self-delete guard doesn't trigger)
        var result = await Controller(db, callerId: Guid.NewGuid()).Delete(adminId, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        db.Users.Find(adminId).Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_LastActiveAdmin_IgnoresInactiveAdmins()
    {
        var db = TestDbFactory.Create();
        var activeAdminId = Guid.NewGuid();
        var inactiveAdmin = AdminUser(Guid.NewGuid());
        inactiveAdmin.Username = "inactive-admin";
        inactiveAdmin.IsActive = false;
        db.Users.AddRange(AdminUser(activeAdminId), inactiveAdmin);
        await db.SaveChangesAsync();

        var result = await Controller(db, callerId: Guid.NewGuid()).Delete(activeAdminId, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        db.Users.Find(activeAdminId).Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_Self_Returns400()
    {
        var db = TestDbFactory.Create();
        var adminId = Guid.NewGuid();
        var admin2Id = Guid.NewGuid();
        db.Users.AddRange(AdminUser(adminId), new User
        {
            Id = admin2Id, Username = "admin2", PasswordHash = "h",
            Role = UserRole.Admin, IsActive = true,
            CreatedAt = DateTime.UtcNow, PasswordChangedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        // caller tries to delete their own account
        var result = await Controller(db, callerId: adminId).Delete(adminId, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Delete_NotFound_Returns404()
    {
        var db = TestDbFactory.Create();
        var result = await Controller(db).Delete(Guid.NewGuid(), CancellationToken.None);
        result.Should().BeOfType<NotFoundResult>();
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Audit emission — every Admin-driven user mutation must leave a trail. These
    // tests assert on the granular action codes (USER_CREATED / USER_ROLE_CHANGED /
    // USER_ACTIVATED / USER_DEACTIVATED / USER_PASSWORD_RESET / USER_DELETED) so
    // that splitting them was a deliberate choice and a future "consolidate into
    // USER_UPDATED" refactor would have to delete a test rather than silently break
    // SIEM rules downstream.
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_ValidUser_EmitsUserCreatedAudit()
    {
        var db = TestDbFactory.Create();
        var (controller, audit) = NewController(db);

        await controller.Create(
            new CreateUserRequest("alice", "Str0ng@Pass!", "Operator"),
            CancellationToken.None);

        var entry = audit.Calls.Should().ContainSingle().Subject;
        entry.Action.Should().Be("USER_CREATED");
        entry.ResourceType.Should().Be("User");
        entry.Details.Should().Contain("alice").And.Contain("Operator");
    }

    [Fact]
    public async Task Create_DuplicateUsername_DoesNotEmitAudit()
    {
        var db = TestDbFactory.Create();
        db.Users.Add(OperatorUser());
        await db.SaveChangesAsync();
        var (controller, audit) = NewController(db);

        await controller.Create(
            new CreateUserRequest("operator", "Str0ng@Pass!", "Operator"),
            CancellationToken.None);

        audit.Calls.Should().BeEmpty("the validation path returned 409 before any state change");
    }

    [Fact]
    public async Task Update_RoleChange_EmitsRoleChangedAudit()
    {
        var db = TestDbFactory.Create();
        var adminId = Guid.NewGuid();
        var operatorId = Guid.NewGuid();
        db.Users.AddRange(AdminUser(adminId), OperatorUser(operatorId));
        await db.SaveChangesAsync();
        var (controller, audit) = NewController(db);

        await controller.Update(operatorId,
            new UpdateUserRequest("Viewer", null, null),
            CancellationToken.None);

        db.Users.Find(operatorId)!.SecurityStamp.Should().Be(1);
        var entry = audit.Calls.Should().ContainSingle().Subject;
        entry.Action.Should().Be("USER_ROLE_CHANGED");
        entry.Details.Should().Contain("Operator").And.Contain("Viewer");
    }

    [Fact]
    public async Task Update_PasswordReset_EmitsPasswordResetAudit()
    {
        var db = TestDbFactory.Create();
        var operatorId = Guid.NewGuid();
        db.Users.Add(OperatorUser(operatorId));
        await db.SaveChangesAsync();
        var (controller, audit) = NewController(db);

        await controller.Update(operatorId,
            new UpdateUserRequest(null, null, "NewStr0ng@Pass!"),
            CancellationToken.None);

        db.Users.Find(operatorId)!.SecurityStamp.Should().Be(1);
        var entry = audit.Calls.Should().ContainSingle().Subject;
        entry.Action.Should().Be("USER_PASSWORD_RESET");
        // The audit must NEVER contain the new password — only the username.
        entry.Details.Should().NotContain("NewStr0ng@Pass!");
    }

    [Fact]
    public async Task Update_Deactivation_EmitsUserDeactivatedAudit()
    {
        var db = TestDbFactory.Create();
        var adminId = Guid.NewGuid();
        var admin2Id = Guid.NewGuid();
        db.Users.AddRange(AdminUser(adminId), new User
        {
            Id = admin2Id, Username = "admin2", PasswordHash = "h",
            Role = UserRole.Admin, IsActive = true,
            CreatedAt = DateTime.UtcNow, PasswordChangedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var (controller, audit) = NewController(db);

        await controller.Update(admin2Id,
            new UpdateUserRequest(null, false, null),
            CancellationToken.None);

        audit.Calls.Should().ContainSingle(e => e.Action == "USER_DEACTIVATED" && e.ResourceId == admin2Id);
    }

    [Fact]
    public async Task Update_MultipleChanges_EmitsMultipleAuditRows()
    {
        var db = TestDbFactory.Create();
        var operatorId = Guid.NewGuid();
        db.Users.Add(OperatorUser(operatorId));
        await db.SaveChangesAsync();
        var (controller, audit) = NewController(db);

        // One PUT changes role + active state + password in one shot; we still expect
        // three separate audit rows so SIEM can independently alert on the password reset.
        await controller.Update(operatorId,
            new UpdateUserRequest("Viewer", false, "NewStr0ng@Pass!"),
            CancellationToken.None);

        audit.Calls.Select(e => e.Action).Should().BeEquivalentTo(new[]
        {
            "USER_ROLE_CHANGED", "USER_DEACTIVATED", "USER_PASSWORD_RESET"
        });
    }

    [Fact]
    public async Task Update_NoActualChange_DoesNotEmitAudit()
    {
        var db = TestDbFactory.Create();
        var operatorId = Guid.NewGuid();
        db.Users.Add(OperatorUser(operatorId));
        await db.SaveChangesAsync();
        var (controller, audit) = NewController(db);

        // Setting role to its current value and IsActive to its current value should
        // NOT emit an audit row — a "noop PUT" must not pollute the audit trail.
        await controller.Update(operatorId,
            new UpdateUserRequest("Operator", true, null),
            CancellationToken.None);

        audit.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_RegularUser_EmitsUserDeletedAudit()
    {
        var db = TestDbFactory.Create();
        var adminId = Guid.NewGuid();
        var operatorId = Guid.NewGuid();
        db.Users.AddRange(AdminUser(adminId), OperatorUser(operatorId));
        await db.SaveChangesAsync();
        var (controller, audit) = NewController(db, callerId: adminId);

        await controller.Delete(operatorId, CancellationToken.None);

        var entry = audit.Calls.Should().ContainSingle().Subject;
        entry.Action.Should().Be("USER_DELETED");
        entry.ResourceId.Should().Be(operatorId);
        // The username snapshot must survive the delete — without it the audit row would
        // be unreadable once the User row is gone.
        entry.Details.Should().Contain("operator");
    }
}
