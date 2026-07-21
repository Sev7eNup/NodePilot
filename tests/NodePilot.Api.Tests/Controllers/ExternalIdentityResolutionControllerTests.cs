using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NodePilot.Api.Controllers;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public sealed class ExternalIdentityResolutionControllerTests
{
    [Fact]
    public async Task ResolveUpgradeConflict_DuplicateLdapObjectGuid_CreatesLegacyIdentityAndOffboardsLoser()
    {
        using var db = TestDbFactory.Create();
        const string objectGuid = "7930fd29-efaf-4c92-a3f0-e59ab2875726";
        var winner = ExternalUser(AuthProvider.Ldap, objectGuid, UserRole.Operator);
        var loser = ExternalUser(AuthProvider.Ldap, objectGuid, UserRole.Viewer);
        loser.KnownGroupSidsJson = "[\"S-1-5-21-1000\"]";
        loser.LastDirectorySyncAt = DateTime.UtcNow;
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "legacy-conflict", DefinitionJson = "{}" };
        var loserExecution = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            StartedByUserId = loser.Id,
            Status = ExecutionStatus.Running,
            StartedAt = DateTime.UtcNow,
        };
        var winnerExecution = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            StartedByUserId = winner.Id,
            Status = ExecutionStatus.Pending,
            StartedAt = DateTime.UtcNow,
        };
        db.AddRange(
            winner,
            loser,
            workflow,
            loserExecution,
            winnerExecution,
            Session(winner.Id),
            Session(loser.Id),
            new DirectoryMembership
            {
                Id = Guid.NewGuid(),
                UserId = loser.Id,
                Authority = ExternalIdentity.ActiveDirectoryAuthority,
                GroupKey = "S-1-5-21-1000",
                LastSeenAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var result = await Controller(db).ResolveUpgradeConflict(
            new ResolveUpgradeIdentityConflictRequest(
                AuthProvider.Ldap,
                objectGuid,
                winner.Id,
                [loser.Id]),
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        db.ChangeTracker.Clear();
        var identity = await db.ExternalIdentities.SingleAsync();
        identity.Should().Match<ExternalIdentity>(candidate =>
            candidate.UserId == winner.Id
            && candidate.Authority == ExternalIdentity.LegacyLdapAuthority
            && candidate.Subject == objectGuid);
        (await db.Users.SingleAsync(user => user.Id == loser.Id)).Should().Match<User>(user =>
            !user.IsActive
            && user.IsTombstoned
            && user.ExternalId == null
            && user.KnownGroupSidsJson == null
            && user.LastDirectorySyncAt == null);
        (await db.AuthSessions.Where(session => session.UserId == winner.Id || session.UserId == loser.Id)
                .ToListAsync())
            .Should().OnlyContain(session => session.RevokedAt != null);
        (await db.WorkflowExecutions.ToListAsync()).Should().HaveCount(2)
            .And.OnlyContain(candidate =>
                candidate.Status == ExecutionStatus.Cancelled
                && candidate.CancelledBy == "identity-conflict-resolution");
        (await db.DirectoryMemberships.AnyAsync(membership => membership.UserId == loser.Id))
            .Should().BeFalse();
        (await db.AuditLog.SingleAsync()).Action
            .Should().Be(AuditActions.UserExternalIdentityResolved);
    }

    [Fact]
    public async Task ListUpgradeConflicts_DiscoversExactLegacyKeysWithoutExternalIdentityRows()
    {
        using var db = TestDbFactory.Create();
        const string objectGuid = "7930fd29-efaf-4c92-a3f0-e59ab2875726";
        const string windowsSid = "S-1-5-21-100-200-300-400";
        var ldapWinner = ExternalUser(AuthProvider.Ldap, objectGuid, UserRole.Operator);
        var ldapLoser = ExternalUser(AuthProvider.Ldap, objectGuid, UserRole.Admin);
        ldapLoser.IsActive = false;
        ldapLoser.IsTombstoned = true;
        var windowsWinner = ExternalUser(AuthProvider.Windows, windowsSid, UserRole.Viewer);
        var windowsLoser = ExternalUser(AuthProvider.Windows, windowsSid, UserRole.Operator);
        var differentCase = ExternalUser(
            AuthProvider.Ldap,
            objectGuid.ToUpperInvariant(),
            UserRole.Viewer);
        var singular = ExternalUser(AuthProvider.Windows, "S-1-5-21-single", UserRole.Viewer);
        var localOne = LocalUser("shared-local-key");
        var localTwo = LocalUser("shared-local-key");
        db.Users.AddRange(
            ldapWinner,
            ldapLoser,
            windowsWinner,
            windowsLoser,
            differentCase,
            singular,
            localOne,
            localTwo);
        await db.SaveChangesAsync();

        var result = await Controller(db).ListUpgradeConflicts(CancellationToken.None);

        var conflicts = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<List<UpgradeIdentityConflict>>().Subject;
        conflicts.Should().HaveCount(2);
        var ldap = conflicts.Single(conflict => conflict.Provider == AuthProvider.Ldap);
        ldap.ConflictExternalId.Should().Be(objectGuid);
        ldap.Candidates.Select(candidate => candidate.Id)
            .Should().BeEquivalentTo([ldapWinner.Id, ldapLoser.Id]);
        ldap.Candidates.Single(candidate => candidate.Id == ldapLoser.Id)
            .Should().Match<UpgradeIdentityConflictCandidate>(candidate =>
                candidate.Role == UserRole.Admin
                && !candidate.IsActive
                && candidate.IsTombstoned
                && candidate.Username == ldapLoser.Username);
        var windows = conflicts.Single(conflict => conflict.Provider == AuthProvider.Windows);
        windows.ConflictExternalId.Should().Be(windowsSid);
        windows.Candidates.Select(candidate => candidate.Id)
            .Should().BeEquivalentTo([windowsWinner.Id, windowsLoser.Id]);
    }

    [Fact]
    public async Task ResolveUpgradeConflict_DuplicateWindowsSid_CreatesCanonicalIdentity()
    {
        using var db = TestDbFactory.Create();
        const string sid = "S-1-5-21-100-200-300-400";
        var winner = ExternalUser(AuthProvider.Windows, sid, UserRole.Operator);
        var loser = ExternalUser(AuthProvider.Windows, sid, UserRole.Viewer);
        db.Users.AddRange(winner, loser);
        await db.SaveChangesAsync();

        var result = await Controller(db).ResolveUpgradeConflict(
            new ResolveUpgradeIdentityConflictRequest(
                AuthProvider.Windows,
                sid,
                winner.Id,
                [loser.Id]),
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        db.ChangeTracker.Clear();
        var identity = await db.ExternalIdentities.SingleAsync();
        identity.UserId.Should().Be(winner.Id);
        identity.Authority.Should().Be(ExternalIdentity.ActiveDirectoryAuthority);
        identity.Subject.Should().Be(sid);
        (await db.Users.SingleAsync(user => user.Id == loser.Id)).Should().Match<User>(user =>
            !user.IsActive && user.IsTombstoned && user.ExternalId == null);
    }

    [Fact]
    public async Task ResolveUpgradeConflict_WouldRetireLastActiveAdmin_IsRejectedAtomically()
    {
        using var db = TestDbFactory.Create();
        var winner = ExternalUser(AuthProvider.Windows, "S-1-5-21-9", UserRole.Operator);
        var lastAdmin = ExternalUser(AuthProvider.Windows, "S-1-5-21-9", UserRole.Admin);
        db.Users.AddRange(winner, lastAdmin);
        await db.SaveChangesAsync();

        var result = await Controller(db).ResolveUpgradeConflict(
            new ResolveUpgradeIdentityConflictRequest(
                AuthProvider.Windows,
                "S-1-5-21-9",
                winner.Id,
                [lastAdmin.Id]),
            CancellationToken.None);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.Value.Should().NotBeNull();
        db.ChangeTracker.Clear();
        (await db.ExternalIdentities.AnyAsync()).Should().BeFalse();
        (await db.Users.SingleAsync(user => user.Id == lastAdmin.Id)).Should().Match<User>(user =>
            user.IsActive && !user.IsTombstoned && user.ExternalId == "S-1-5-21-9");
        (await db.AuditLog.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ResolveAdConflict_LastLocalBreakGlassOwner_IsNotAnEligibleCandidate()
    {
        using var db = TestDbFactory.Create();
        const string sid = "S-1-5-21-42";
        const string objectGuid = "legacy-guid";
        var windows = ExternalUser(AuthProvider.Windows, sid, UserRole.Operator);
        var recovery = new User
        {
            Id = Guid.NewGuid(),
            Username = "recovery",
            Provider = AuthProvider.Local,
            ExternalId = objectGuid,
            PasswordHash = "hash",
            Role = UserRole.Admin,
            IsActive = true,
            IsBreakGlass = true,
            CreatedAt = DateTime.UtcNow,
            PasswordChangedAt = DateTime.UtcNow,
        };
        db.Users.AddRange(windows, recovery);
        db.ExternalIdentities.AddRange(
            Identity(windows.Id, ExternalIdentity.ActiveDirectoryAuthority, sid),
            Identity(recovery.Id, ExternalIdentity.LegacyLdapAuthority, objectGuid));
        await db.SaveChangesAsync();

        var result = await Controller(db).ResolveAdConflict(
            new ResolveAdIdentityConflictRequest(sid, objectGuid, windows.Id),
            CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        db.ChangeTracker.Clear();
        (await db.Users.SingleAsync(user => user.Id == recovery.Id)).Should().Match<User>(user =>
            user.IsActive && user.IsBreakGlass && !user.IsTombstoned && user.ExternalId == objectGuid);
        (await db.ExternalIdentities.CountAsync()).Should().Be(2);
        (await db.AuditLog.AnyAsync()).Should().BeFalse();
    }

    private static ExternalIdentityResolutionController Controller(NodePilotDbContext db)
    {
        var controller = new ExternalIdentityResolutionController(
            db,
            new AuditStager(),
            new MemoryCache(new MemoryCacheOptions()));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim(ClaimTypes.Name, "sso-admin"),
                        new Claim(ClaimTypes.Role, "Admin"),
                    ],
                    "test")),
            },
        };
        return controller;
    }

    private static User ExternalUser(AuthProvider provider, string externalId, UserRole role) => new()
    {
        Id = Guid.NewGuid(),
        Username = $"{provider}-{Guid.NewGuid():N}@example.test",
        Provider = provider,
        ExternalId = externalId,
        PasswordHash = null,
        Role = role,
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        PasswordChangedAt = DateTime.UtcNow,
    };

    private static ExternalIdentity Identity(Guid userId, string authority, string subject) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Authority = authority,
        Subject = subject,
        CreatedAt = DateTime.UtcNow,
        LastSeenAt = DateTime.UtcNow,
    };

    private static User LocalUser(string externalId) => new()
    {
        Id = Guid.NewGuid(),
        Username = $"local-{Guid.NewGuid():N}",
        Provider = AuthProvider.Local,
        ExternalId = externalId,
        PasswordHash = "hash",
        Role = UserRole.Viewer,
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        PasswordChangedAt = DateTime.UtcNow,
    };

    private static AuthSession Session(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        AuthenticationMethod = "sso",
        CreatedAt = DateTime.UtcNow,
        LastSeenAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddHours(1),
        CurrentJti = Guid.NewGuid().ToString("N"),
    };
}
