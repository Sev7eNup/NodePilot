using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NodePilot.Api.Audit;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.Security.Ldap;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Security.Ldap;

public sealed class ExternalUserMapperTests : IDisposable
{
    private const string DefaultAllowedGroup = "S-1-5-21-1-1-1-9999";
    private readonly Microsoft.Data.Sqlite.SqliteConnection _conn;
    private readonly NodePilotDbContext _db;
    private readonly CapturingAuditWriter _audit = new();

    public ExternalUserMapperTests()
    {
        var (conn, db) = TestDbFactory.CreateWithConnection();
        _conn = conn;
        _db = db;
    }

    /// <summary>
    /// Seeds an existing admin user so the empty-DB bootstrap-gate (PR10) doesn't refuse
    /// non-Admin JIT-provisioning. Tests that exercise the JIT happy path must call this
    /// or set themselves up to resolve to <see cref="UserRole.Admin"/>.
    /// </summary>
    private async Task SeedExistingAdminAsync()
    {
        _db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = "preexisting-admin",
            PasswordHash = "$2a$12$dummy",
            Provider = AuthProvider.Local,
            Role = UserRole.Admin,
            IsActive = true,
            IsBreakGlass = true,
            PasswordChangedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private ExternalUserMapper NewMapper(LdapOptions? options = null) =>
        new(_db,
            new StaticOptionsMonitor<LdapOptions>(options ?? DefaultOptions()),
            _audit,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ExternalUserMapper>.Instance);

    private static LdapOptions DefaultOptions(
        SharedFolderRole? jitDefault = null,
        params (string Sid, UserRole Role)[] mappings) =>
        new()
        {
            Enabled = true,
            UpnSuffix = "firma.de",
            JitUserDefaultRootRole = jitDefault,
            AllowedGroupSids = [DefaultAllowedGroup],
            GlobalRoleMappings = mappings.Select(m => new GlobalRoleMapping
            {
                GroupSid = m.Sid,
                Role = m.Role,
            }).ToList(),
        };

    private static LdapAuthResult Sample(string upn = "alice@firma.de", string externalId = "guid-aaa", params string[] groupSids) =>
        new(externalId, upn, "Alice Example", [DefaultAllowedGroup, .. groupSids]);

    [Fact]
    public async Task FreshUser_CreatedWithViewerByDefault_AndAuditLogged()
    {
        await SeedExistingAdminAsync(); // bypass the empty-DB bootstrap gate
        var mapper = NewMapper();
        var ldap = Sample();

        var outcome = await mapper.MapAsync(ldap, default);

        outcome.Result.Should().Be(ExternalUserMapResult.Mapped);
        outcome.User.Should().NotBeNull();
        outcome.User!.Provider.Should().Be(AuthProvider.Ldap);
        outcome.User.ExternalId.Should().Be(ldap.ExternalId);
        outcome.User.Username.Should().Be(ldap.Upn);
        outcome.User.PasswordHash.Should().BeNull();
        outcome.User.Role.Should().Be(UserRole.Viewer);
        outcome.User.IsActive.Should().BeTrue();
        outcome.User.KnownGroupSidsJson.Should().Contain(DefaultAllowedGroup);

        var audit = _audit.Calls.Should().ContainSingle(c => c.Action == "USER_LDAP_JIT_CREATED").Subject;
        using var details = System.Text.Json.JsonDocument.Parse(audit.Details!);
        details.RootElement.GetProperty("oldRole").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        details.RootElement.GetProperty("newRole").GetString().Should().Be("Viewer");
        // Two rows now: the seeded admin + the JIT-provisioned LDAP user.
        (await _db.Users.CountAsync()).Should().Be(2);
        var identity = await _db.ExternalIdentities.SingleAsync(i => i.UserId == outcome.User.Id);
        identity.Authority.Should().Be(ExternalIdentity.ActiveDirectoryAuthority);
        identity.Subject.Should().Be(ldap.Subject);
    }

    [Fact]
    public async Task FreshUser_RoleFromGlobalMappings()
    {
        await SeedExistingAdminAsync();
        var sid = "S-1-5-21-1-1-1-512";
        var mapper = NewMapper(DefaultOptions(mappings: (sid, UserRole.Admin)));
        var ldap = Sample(groupSids: sid);

        var outcome = await mapper.MapAsync(ldap, default);

        outcome.User!.Role.Should().Be(UserRole.Admin);
    }

    [Fact]
    public async Task FreshUser_DefaultJitRootRole_GrantsFolderPermission()
    {
        await SeedExistingAdminAsync();
        var mapper = NewMapper(DefaultOptions(jitDefault: SharedFolderRole.FolderViewer));
        var ldap = Sample();

        var outcome = await mapper.MapAsync(ldap, default);

        var grant = await _db.SharedFolderPermissions.SingleAsync();
        grant.FolderId.Should().Be(SharedWorkflowFolder.RootFolderId);
        grant.PrincipalType.Should().Be(FolderPrincipalType.User);
        grant.PrincipalKey.Should().Be(outcome.User!.Id.ToString("D"));
        grant.Role.Should().Be(SharedFolderRole.FolderViewer);
        grant.GrantedByUserId.Should().BeNull();
    }

    [Fact]
    public async Task FreshUser_AdminRoleSkipsJitFolderGrant()
    {
        // Admin bypasses folder ACLs anyway — emitting a Root grant would be noise.
        await SeedExistingAdminAsync();
        var sid = "S-1-5-21-1-1-1-512";
        var mapper = NewMapper(DefaultOptions(jitDefault: SharedFolderRole.FolderViewer, mappings: (sid, UserRole.Admin)));
        var ldap = Sample(groupSids: sid);

        await mapper.MapAsync(ldap, default);

        (await _db.SharedFolderPermissions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ExistingMatchByExternalId_UpdatesRoleAndGroupSids()
    {
        // Pre-existing JIT row with stale role + groups.
        var existing = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice@firma.de",
            Provider = AuthProvider.Ldap,
            ExternalId = "guid-aaa",
            KnownGroupSidsJson = "[\"old-sid\"]",
            Role = UserRole.Viewer,
            IsActive = true,
            SecurityStamp = 3,
        };
        _db.Users.Add(existing);
        await _db.SaveChangesAsync();

        var newSid = "S-1-5-21-1-1-1-700";
        var mapper = NewMapper(DefaultOptions(mappings: (newSid, UserRole.Operator)));
        var outcome = await mapper.MapAsync(Sample(groupSids: newSid), default);

        outcome.User!.Id.Should().Be(existing.Id);
        outcome.User.Role.Should().Be(UserRole.Operator);
        outcome.User.SecurityStamp.Should().Be(4);
        outcome.User.KnownGroupSidsJson.Should().Contain(newSid);
        (await _db.AuditLog.CountAsync(c => c.Action == "USER_LDAP_JIT_UPDATED"))
            .Should().Be(1);
    }

    [Fact]
    public async Task ExistingSoleActiveAdmin_DemotionIsRefused_AndAuditRecordsRoleAndGroupDelta()
    {
        const string adminSid = "S-1-5-21-1-1-1-512";
        var existing = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice@firma.de",
            Provider = AuthProvider.Ldap,
            ExternalId = "guid-aaa",
            KnownGroupSidsJson = $"[\"{adminSid}\"]",
            Role = UserRole.Admin,
            IsActive = true,
            SecurityStamp = 7,
        };
        _db.Users.Add(existing);
        await _db.SaveChangesAsync();

        var mapper = NewMapper(DefaultOptions(mappings: (adminSid, UserRole.Admin)));
        var outcome = await mapper.MapAsync(Sample(groupSids: "S-1-5-21-1-1-1-513"), default);

        outcome.Result.Should().Be(ExternalUserMapResult.RefusedLastActiveAdmin);
        outcome.User.Should().BeNull();

        var persisted = await _db.Users.AsNoTracking().SingleAsync(u => u.Id == existing.Id);
        persisted.Role.Should().Be(UserRole.Admin);
        persisted.SecurityStamp.Should().Be(8, "the refused login must invalidate existing Admin sessions");

        var audit = await _db.AuditLog.SingleAsync(c =>
            c.Action == "USER_LDAP_REFUSED_LAST_ADMIN");
        using var details = System.Text.Json.JsonDocument.Parse(audit.Details!);
        details.RootElement.GetProperty("oldRole").GetString().Should().Be("Admin");
        details.RootElement.GetProperty("newRole").GetString().Should().Be("Viewer");
        details.RootElement.GetProperty("removedGroupSids").EnumerateArray()
            .Select(x => x.GetString()).Should().Contain(adminSid);
        details.RootElement.GetProperty("addedGroupSids").EnumerateArray()
            .Select(x => x.GetString()).Should().Contain("S-1-5-21-1-1-1-513");
    }

    [Fact]
    public async Task ExistingMatch_GroupMembershipChangesWithoutRoleChange_InvalidatesSessions()
    {
        const string oldSid = "S-1-5-21-1-1-1-700";
        const string newSid = "S-1-5-21-1-1-1-701";
        var existing = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice@firma.de",
            Provider = AuthProvider.Ldap,
            ExternalId = "guid-aaa",
            KnownGroupSidsJson = $"[\"{oldSid}\"]",
            Role = UserRole.Viewer,
            IsActive = true,
            SecurityStamp = 11,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(), UserId = existing.Id, AuthenticationMethod = "Ldap",
            CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1), AuthorizationVersion = 11,
            CurrentJti = Guid.NewGuid().ToString("N"),
        };
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(), Name = "scheduled", DefinitionJson = "{}",
        };
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, TriggeredBy = "scheduleTrigger",
            StartedByUserId = existing.Id, Status = ExecutionStatus.Pending,
            StartedAt = DateTime.UtcNow,
        };
        _db.AddRange(existing,
            new ExternalIdentity
            {
                Id = Guid.NewGuid(), UserId = existing.Id,
                Authority = ExternalIdentity.ActiveDirectoryAuthority, Subject = "guid-aaa",
            },
            new DirectoryMembership
            {
                UserId = existing.Id, Authority = ExternalIdentity.ActiveDirectoryAuthority,
                GroupKey = oldSid, LastSeenAt = DateTime.UtcNow,
            },
            session, workflow, execution);
        await _db.SaveChangesAsync();

        var outcome = await NewMapper().MapAsync(Sample(groupSids: newSid), default);

        outcome.Result.Should().Be(ExternalUserMapResult.Mapped);
        outcome.User!.Role.Should().Be(UserRole.Viewer);
        outcome.User.SecurityStamp.Should().Be(12);
        (await _db.AuthSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id))
            .RevokedAt.Should().NotBeNull();
        (await _db.WorkflowExecutions.AsNoTracking().SingleAsync(x => x.Id == execution.Id))
            .Status.Should().Be(ExecutionStatus.Cancelled);

        var audit = await _db.AuditLog.SingleAsync(c => c.Action == "USER_LDAP_JIT_UPDATED");
        using var details = System.Text.Json.JsonDocument.Parse(audit.Details!);
        details.RootElement.GetProperty("oldRole").GetString().Should().Be("Viewer");
        details.RootElement.GetProperty("newRole").GetString().Should().Be("Viewer");
        details.RootElement.GetProperty("removedGroupSids").EnumerateArray()
            .Select(x => x.GetString()).Should().Contain(oldSid);
        details.RootElement.GetProperty("addedGroupSids").EnumerateArray()
            .Select(x => x.GetString()).Should().Contain(newSid);
    }

    [Fact]
    public async Task SameRoleGroupRemoval_AuditStagingFailure_RollsBackSnapshotAndOffboarding()
    {
        const string oldSid = "S-1-5-21-1-1-1-700";
        const string newSid = "S-1-5-21-1-1-1-701";
        var existing = new User
        {
            Id = Guid.NewGuid(), Username = "alice@firma.de", Provider = AuthProvider.Ldap,
            ExternalId = "guid-aaa", KnownGroupSidsJson = $"[\"{oldSid}\"]",
            Role = UserRole.Viewer, IsActive = true, SecurityStamp = 11,
            LastDirectorySyncAt = DateTime.UtcNow.AddMinutes(-5),
            DirectorySyncStatus = "Current",
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(), UserId = existing.Id, AuthenticationMethod = "Ldap",
            CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1), CurrentJti = Guid.NewGuid().ToString("N"),
        };
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(), Name = "same-role rollback", DefinitionJson = "{}",
        };
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, StartedByUserId = existing.Id,
            Status = ExecutionStatus.Pending, StartedAt = DateTime.UtcNow,
            TriggeredBy = "scheduleTrigger",
        };
        _db.AddRange(existing,
            new ExternalIdentity
            {
                Id = Guid.NewGuid(), UserId = existing.Id,
                Authority = ExternalIdentity.ActiveDirectoryAuthority, Subject = "guid-aaa",
            },
            new DirectoryMembership
            {
                UserId = existing.Id, Authority = ExternalIdentity.ActiveDirectoryAuthority,
                GroupKey = oldSid, LastSeenAt = DateTime.UtcNow,
            },
            session, workflow, execution);
        await _db.SaveChangesAsync();
        var engine = new Mock<IWorkflowEngine>();
        var mapper = new ExternalUserMapper(
            _db,
            new StaticOptionsMonitor<LdapOptions>(DefaultOptions()),
            _audit,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ExternalUserMapper>.Instance,
            workflowEngine: engine.Object,
            auditStager: new ThrowingAuditStager());

        var map = () => mapper.MapAsync(Sample(groupSids: newSid), default);

        await map.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("audit staging failed");
        _db.ChangeTracker.Clear();
        var persisted = await _db.Users.SingleAsync(candidate => candidate.Id == existing.Id);
        persisted.KnownGroupSidsJson.Should().Be($"[\"{oldSid}\"]");
        persisted.SecurityStamp.Should().Be(11);
        (await _db.DirectoryMemberships.SingleAsync(candidate => candidate.UserId == existing.Id))
            .GroupKey.Should().Be(oldSid);
        (await _db.AuthSessions.SingleAsync(candidate => candidate.Id == session.Id))
            .RevokedAt.Should().BeNull();
        (await _db.WorkflowExecutions.SingleAsync(candidate => candidate.Id == execution.Id))
            .Status.Should().Be(ExecutionStatus.Pending);
        (await _db.AuditLog.CountAsync()).Should().Be(0);
        engine.Verify(candidate => candidate.CancelAsync(
            It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LoginSnapshotWithoutAllowedGroup_ImmediatelyOffboardsKnownIdentity()
    {
        const string subject = "S-1-5-21-1-2-3-1001";
        const string allowed = "S-1-5-21-1-2-3-2001";
        const string removed = "S-1-5-21-1-2-3-2002";
        var user = new User
        {
            Id = Guid.NewGuid(), Username = "alice@firma.de", Provider = AuthProvider.Ldap,
            ExternalId = subject, Role = UserRole.Operator, IsActive = true,
            KnownGroupSidsJson = $"[\"{allowed}\"]",
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(), UserId = user.Id, AuthenticationMethod = "Ldap",
            CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1), CurrentJti = Guid.NewGuid().ToString("N"),
        };
        _db.AddRange(user,
            new ExternalIdentity
            {
                Id = Guid.NewGuid(), UserId = user.Id,
                Authority = ExternalIdentity.ActiveDirectoryAuthority, Subject = subject,
            },
            new DirectoryMembership
            {
                UserId = user.Id, Authority = ExternalIdentity.ActiveDirectoryAuthority,
                GroupKey = allowed, LastSeenAt = DateTime.UtcNow,
            },
            session);
        await _db.SaveChangesAsync();
        var options = DefaultOptions();
        options.AllowedGroupSids = [allowed];

        var result = await NewMapper(options).MapAsync(
            new LdapAuthResult(subject, user.Username, "Alice", [removed]), default);

        result.Result.Should().Be(ExternalUserMapResult.RefusedDirectoryAccess);
        _db.ChangeTracker.Clear();
        var persisted = await _db.Users.SingleAsync(x => x.Id == user.Id);
        persisted.IsActive.Should().BeFalse();
        persisted.DirectorySyncStatus.Should().Be("AccessRevoked");
        persisted.SecurityStamp.Should().Be(1);
        (await _db.AuthSessions.SingleAsync(x => x.Id == session.Id)).RevokedAt.Should().NotBeNull();
        (await _db.DirectoryMemberships.SingleAsync(x => x.UserId == user.Id)).GroupKey
            .Should().Be(removed);
        var audit = await _db.AuditLog.SingleAsync(entry =>
            entry.Action == AuditActions.UserDirectoryAccessRefused);
        audit.ResourceId.Should().Be(user.Id);
    }

    [Fact]
    public async Task KnownIdentityOffboarding_AuditStagingFailure_RollsBackEverySecurityMutation()
    {
        const string subject = "S-1-5-21-1-2-3-1001";
        const string allowed = "S-1-5-21-1-2-3-2001";
        const string removed = "S-1-5-21-1-2-3-2002";
        var user = new User
        {
            Id = Guid.NewGuid(), Username = "alice@firma.de", Provider = AuthProvider.Ldap,
            ExternalId = subject, Role = UserRole.Operator, IsActive = true,
            KnownGroupSidsJson = $"[\"{allowed}\"]",
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(), UserId = user.Id, AuthenticationMethod = "Ldap",
            CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1), CurrentJti = Guid.NewGuid().ToString("N"),
        };
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(), Name = "offboarding rollback", DefinitionJson = "{}",
        };
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, StartedByUserId = user.Id,
            Status = ExecutionStatus.Running, StartedAt = DateTime.UtcNow,
            TriggeredBy = "scheduleTrigger",
        };
        _db.AddRange(user,
            new ExternalIdentity
            {
                Id = Guid.NewGuid(), UserId = user.Id,
                Authority = ExternalIdentity.ActiveDirectoryAuthority, Subject = subject,
            },
            new DirectoryMembership
            {
                UserId = user.Id, Authority = ExternalIdentity.ActiveDirectoryAuthority,
                GroupKey = allowed, LastSeenAt = DateTime.UtcNow,
            },
            session, workflow, execution);
        await _db.SaveChangesAsync();
        var options = DefaultOptions();
        options.AllowedGroupSids = [allowed];
        var engine = new Mock<IWorkflowEngine>();
        var mapper = new ExternalUserMapper(
            _db,
            new StaticOptionsMonitor<LdapOptions>(options),
            _audit,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ExternalUserMapper>.Instance,
            workflowEngine: engine.Object,
            auditStager: new ThrowingAuditStager());

        var offboard = () => mapper.MapAsync(
            new LdapAuthResult(subject, user.Username, "Alice", [removed]), default);

        await offboard.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("audit staging failed");
        _db.ChangeTracker.Clear();
        var persisted = await _db.Users.SingleAsync(candidate => candidate.Id == user.Id);
        persisted.IsActive.Should().BeTrue();
        persisted.SecurityStamp.Should().Be(0);
        persisted.KnownGroupSidsJson.Should().Be($"[\"{allowed}\"]");
        (await _db.AuthSessions.SingleAsync(candidate => candidate.Id == session.Id))
            .RevokedAt.Should().BeNull();
        (await _db.DirectoryMemberships.SingleAsync(candidate => candidate.UserId == user.Id))
            .GroupKey.Should().Be(allowed);
        (await _db.WorkflowExecutions.SingleAsync(candidate => candidate.Id == execution.Id))
            .Status.Should().Be(ExecutionStatus.Running);
        (await _db.AuditLog.CountAsync()).Should().Be(0);
        engine.Verify(candidate => candidate.CancelAsync(
            It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task KnownIdentityOffboarding_ClientAbortAfterCommit_DoesNotCancelEngineSignal()
    {
        const string subject = "S-1-5-21-1-2-3-1001";
        const string allowed = "S-1-5-21-1-2-3-2001";
        const string removed = "S-1-5-21-1-2-3-2002";
        var user = new User
        {
            Id = Guid.NewGuid(), Username = "alice@firma.de", Provider = AuthProvider.Ldap,
            ExternalId = subject, Role = UserRole.Operator, IsActive = true,
            KnownGroupSidsJson = $"[\"{allowed}\"]",
        };
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(), Name = "postcommit signal", DefinitionJson = "{}",
        };
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, StartedByUserId = user.Id,
            Status = ExecutionStatus.Running, StartedAt = DateTime.UtcNow,
            TriggeredBy = "scheduleTrigger",
        };
        _db.AddRange(user,
            new ExternalIdentity
            {
                Id = Guid.NewGuid(), UserId = user.Id,
                Authority = ExternalIdentity.ActiveDirectoryAuthority, Subject = subject,
            },
            workflow, execution);
        await _db.SaveChangesAsync();
        var options = DefaultOptions();
        options.AllowedGroupSids = [allowed];
        using var requestAbort = new CancellationTokenSource();
        CancellationToken engineToken = default;
        var engine = new Mock<IWorkflowEngine>();
        engine.Setup(candidate => candidate.CancelAsync(
                execution.Id, "directory-authorization-change", It.IsAny<CancellationToken>()))
            .Callback<Guid, string?, CancellationToken>((_, _, token) => engineToken = token)
            .ReturnsAsync(true);
        var mapper = new ExternalUserMapper(
            _db,
            new StaticOptionsMonitor<LdapOptions>(options),
            _audit,
            new MemoryCache(new MemoryCacheOptions()),
            new CancelOnFirstLogLogger<ExternalUserMapper>(requestAbort),
            workflowEngine: engine.Object);

        var result = await mapper.MapAsync(
            new LdapAuthResult(subject, user.Username, "Alice", [removed]),
            requestAbort.Token);

        result.Result.Should().Be(ExternalUserMapResult.RefusedDirectoryAccess);
        requestAbort.IsCancellationRequested.Should().BeTrue();
        engine.Verify(candidate => candidate.CancelAsync(
            execution.Id, "directory-authorization-change", It.IsAny<CancellationToken>()), Times.Once);
        engineToken.CanBeCanceled.Should().BeTrue();
        engineToken.IsCancellationRequested.Should().BeFalse();
        engineToken.Should().NotBe(requestAbort.Token);
    }

    [Fact]
    public async Task KnownIdentityOffboarding_ReloadsAfterConcurrentAdminReactivation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nodepilot-ldap-reactivate-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        try
        {
            const string subject = "S-1-5-21-1-2-3-1001";
            const string allowed = "S-1-5-21-1-2-3-2001";
            const string removed = "S-1-5-21-1-2-3-2002";
            var userId = Guid.NewGuid();
            await using (var seed = new NodePilotDbContext(options))
            {
                await seed.Database.EnsureCreatedAsync();
                seed.AddRange(
                    new User
                    {
                        Id = userId, Username = "alice@firma.de", Provider = AuthProvider.Ldap,
                        ExternalId = subject, Role = UserRole.Operator, IsActive = false,
                        DirectorySyncStatus = "AccessRevoked",
                        KnownGroupSidsJson = $"[\"{allowed}\"]",
                    },
                    new ExternalIdentity
                    {
                        Id = Guid.NewGuid(), UserId = userId,
                        Authority = ExternalIdentity.ActiveDirectoryAuthority, Subject = subject,
                    },
                    new DirectoryMembership
                    {
                        UserId = userId, Authority = ExternalIdentity.ActiveDirectoryAuthority,
                        GroupKey = allowed, LastSeenAt = DateTime.UtcNow,
                    });
                await seed.SaveChangesAsync();
            }

            await using var adminDb = new NodePilotDbContext(options);
            await using var loginDb = new NodePilotDbContext(options);
            var blockingAudit = new BlockingAuditWriter();
            var controller = new UsersController(
                adminDb, blockingAudit, new MemoryCache(new MemoryCacheOptions()));
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim(ClaimTypes.Role, UserRole.Admin.ToString()),
                    ], "TestAuth")),
                },
            };
            var ldapOptions = DefaultOptions();
            ldapOptions.AllowedGroupSids = [allowed];
            var mapper = new ExternalUserMapper(
                loginDb,
                new StaticOptionsMonitor<LdapOptions>(ldapOptions),
                NoopAuditWriter.Instance,
                new MemoryCache(new MemoryCacheOptions()),
                NullLogger<ExternalUserMapper>.Instance);

            var reactivateTask = controller.Update(
                userId, new UpdateUserRequest(null, true, null), CancellationToken.None);
            await blockingAudit.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            var deniedLoginTask = mapper.MapAsync(
                new LdapAuthResult(subject, "alice@firma.de", "Alice", [removed]),
                CancellationToken.None);
            blockingAudit.Release();

            (await reactivateTask).Should().BeOfType<NoContentResult>();
            (await deniedLoginTask).Result.Should().Be(ExternalUserMapResult.RefusedDirectoryAccess);
            await using var verify = new NodePilotDbContext(options);
            var persisted = await verify.Users.SingleAsync(candidate => candidate.Id == userId);
            persisted.IsActive.Should().BeFalse();
            persisted.DirectorySyncStatus.Should().Be("AccessRevoked");
            (await verify.DirectoryMemberships.SingleAsync(candidate => candidate.UserId == userId))
                .GroupKey.Should().Be(removed);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ExistingMatch_LargeGroupDelta_AuditRemainsBoundedAndReconstructable()
    {
        var oldGroupSids = Enumerable.Range(0, 20)
            .Select(i => $"S-1-5-21-{new string('1', 140)}-{i:D2}")
            .ToArray();
        var newGroupSids = Enumerable.Range(0, 20)
            .Select(i => $"S-1-5-21-{new string('2', 140)}-{i:D2}")
            .ToArray();
        _db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = "alice@firma.de",
            Provider = AuthProvider.Ldap,
            ExternalId = "guid-aaa",
            KnownGroupSidsJson = System.Text.Json.JsonSerializer.Serialize(oldGroupSids),
            Role = UserRole.Viewer,
            IsActive = true,
        });
        await _db.SaveChangesAsync();

        await NewMapper().MapAsync(Sample(groupSids: newGroupSids), default);

        var audit = await _db.AuditLog.SingleAsync(c => c.Action == "USER_LDAP_JIT_UPDATED");
        audit.Details!.Length.Should().BeLessThan(4096);
        using var details = System.Text.Json.JsonDocument.Parse(audit.Details);
        details.RootElement.GetProperty("addedGroupSidsCount").GetInt32().Should().Be(21);
        details.RootElement.GetProperty("removedGroupSidsCount").GetInt32().Should().Be(20);
        details.RootElement.GetProperty("addedGroupSids").GetArrayLength().Should().Be(6);
        details.RootElement.GetProperty("removedGroupSids").GetArrayLength().Should().Be(6);
        details.RootElement.GetProperty("groupSidDeltaTruncated").GetBoolean().Should().BeTrue();
        details.RootElement.GetProperty("oldGroupSidsHash").GetString().Should().NotBe(
            details.RootElement.GetProperty("newGroupSidsHash").GetString());
    }

    [Fact]
    public async Task EmptyAllowedGroupConfiguration_FailsClosed()
    {
        await SeedExistingAdminAsync();
        var options = DefaultOptions();
        options.AllowedGroupSids = [];

        var outcome = await NewMapper(options).MapAsync(Sample(), default);

        outcome.Result.Should().Be(ExternalUserMapResult.RefusedDirectoryAccess);
        (await _db.ExternalIdentities.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ExistingAdmin_WithAnotherActiveAdmin_DemotionSucceeds()
    {
        const string adminSid = "S-1-5-21-1-1-1-512";
        var externalAdmin = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice@firma.de",
            Provider = AuthProvider.Ldap,
            ExternalId = "guid-aaa",
            KnownGroupSidsJson = $"[\"{adminSid}\"]",
            Role = UserRole.Admin,
            IsActive = true,
            SecurityStamp = 2,
        };
        var recoveryAdmin = new User
        {
            Id = Guid.NewGuid(),
            Username = "recovery-admin",
            Provider = AuthProvider.Local,
            PasswordHash = "$2a$12$dummy",
            Role = UserRole.Admin,
            IsActive = true,
            IsBreakGlass = true,
        };
        _db.Users.AddRange(externalAdmin, recoveryAdmin);
        await _db.SaveChangesAsync();

        var mapper = NewMapper(DefaultOptions(mappings: (adminSid, UserRole.Admin)));
        var outcome = await mapper.MapAsync(Sample(), default);

        outcome.Result.Should().Be(ExternalUserMapResult.Mapped);
        outcome.User!.Role.Should().Be(UserRole.Viewer);
        outcome.User.SecurityStamp.Should().Be(3);

        var audit = await _db.AuditLog.SingleAsync(c => c.Action == "USER_LDAP_JIT_UPDATED");
        using var details = System.Text.Json.JsonDocument.Parse(audit.Details!);
        details.RootElement.GetProperty("oldRole").GetString().Should().Be("Admin");
        details.RootElement.GetProperty("newRole").GetString().Should().Be("Viewer");
    }

    [Fact]
    public async Task ExistingAdminDemotion_WithProductionAuditWriter_PersistsMutationAndAudit()
    {
        const string adminSid = "S-1-5-21-1-1-1-512";
        var externalAdmin = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice@firma.de",
            Provider = AuthProvider.Ldap,
            ExternalId = "guid-aaa",
            KnownGroupSidsJson = $"[\"{adminSid}\"]",
            Role = UserRole.Admin,
            IsActive = true,
        };
        _db.Users.AddRange(
            externalAdmin,
            new User
            {
                Id = Guid.NewGuid(), Username = "recovery-admin", Provider = AuthProvider.Local,
                PasswordHash = "$2a$12$dummy", Role = UserRole.Admin, IsActive = true, IsBreakGlass = true,
            });
        await _db.SaveChangesAsync();

        var auditWriter = new NodePilot.Api.Audit.AuditWriter(
            _db,
            new Microsoft.AspNetCore.Http.HttpContextAccessor(),
            NullLogger<NodePilot.Api.Audit.AuditWriter>.Instance,
            new NodePilot.Core.Audit.AuditStager());
        var mapper = new ExternalUserMapper(
            _db,
            new StaticOptionsMonitor<LdapOptions>(DefaultOptions(mappings: (adminSid, UserRole.Admin))),
            auditWriter,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ExternalUserMapper>.Instance);

        var outcome = await mapper.MapAsync(Sample(), default);

        outcome.Result.Should().Be(ExternalUserMapResult.Mapped);
        (await _db.Users.AsNoTracking().SingleAsync(u => u.Id == externalAdmin.Id)).Role
            .Should().Be(UserRole.Viewer);
        var audit = await _db.AuditLog.AsNoTracking()
            .SingleAsync(entry => entry.Action == "USER_LDAP_JIT_UPDATED");
        using var details = System.Text.Json.JsonDocument.Parse(audit.Details!);
        details.RootElement.GetProperty("oldRole").GetString().Should().Be("Admin");
        details.RootElement.GetProperty("newRole").GetString().Should().Be("Viewer");
    }

    [Fact]
    public async Task ConcurrentAdminDemotions_LeaveExactlyOneActiveAdmin()
    {
        var databaseName = $"jit-last-admin-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared";
        await using var keeper = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await keeper.OpenAsync();

        var options = new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connectionString)
            .Options;
        await using (var seed = new NodePilotDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Users.AddRange(
                new User
                {
                    Id = Guid.NewGuid(), Username = "admin-a@firma.de", Provider = AuthProvider.Ldap,
                    ExternalId = "guid-admin-a", Role = UserRole.Admin, IsActive = true,
                    KnownGroupSidsJson = "[]",
                },
                new User
                {
                    Id = Guid.NewGuid(), Username = "admin-b@firma.de", Provider = AuthProvider.Ldap,
                    ExternalId = "guid-admin-b", Role = UserRole.Admin, IsActive = true,
                    KnownGroupSidsJson = "[]",
                });
            await seed.SaveChangesAsync();
        }

        await using var dbA = new NodePilotDbContext(options);
        await using var dbB = new NodePilotDbContext(options);
        var optionsMonitor = new StaticOptionsMonitor<LdapOptions>(DefaultOptions());
        var mapperA = new ExternalUserMapper(
            dbA, optionsMonitor, new CapturingAuditWriter(), new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ExternalUserMapper>.Instance);
        var mapperB = new ExternalUserMapper(
            dbB, optionsMonitor, new CapturingAuditWriter(), new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ExternalUserMapper>.Instance);

        var results = await Task.WhenAll(
            mapperA.MapAsync(Sample("admin-a@firma.de", "guid-admin-a"), default),
            mapperB.MapAsync(Sample("admin-b@firma.de", "guid-admin-b"), default));

        results.Select(r => r.Result).Should().ContainSingle(r => r == ExternalUserMapResult.Mapped);
        results.Select(r => r.Result).Should().ContainSingle(r => r == ExternalUserMapResult.RefusedLastActiveAdmin);

        await using var verify = new NodePilotDbContext(options);
        (await verify.Users.CountAsync(u => u.Role == UserRole.Admin && u.IsActive)).Should().Be(1);
        (await verify.Users.CountAsync(u => u.Role == UserRole.Viewer && u.IsActive)).Should().Be(1);
    }

    [Fact]
    public async Task ExistingMatch_DeactivatedUser_StaysDeactivated()
    {
        var existing = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice@firma.de",
            Provider = AuthProvider.Ldap,
            ExternalId = "guid-aaa",
            Role = UserRole.Viewer,
            IsActive = false, // Admin deactivated this user
        };
        _db.Users.Add(existing);
        await _db.SaveChangesAsync();

        var mapper = NewMapper();
        var outcome = await mapper.MapAsync(Sample(), default);

        outcome.User!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task UsernameCollisionWithLocalUser_Refuses()
    {
        // Pre-existing local user with the same username (a local "alice@firma.de" — unusual
        // but possible). Identities are never merged → must refuse.
        var local = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice@firma.de",
            Provider = AuthProvider.Local,
            PasswordHash = "$2a$12$irrelevanthash",
            Role = UserRole.Operator,
        };
        _db.Users.Add(local);
        await _db.SaveChangesAsync();

        var mapper = NewMapper(DefaultOptions());
        var outcome = await mapper.MapAsync(Sample(), default);

        outcome.Result.Should().Be(ExternalUserMapResult.RefusedUsernameCollision);
        outcome.User.Should().BeNull();
        _audit.Calls.Should().ContainSingle(c => c.Action == "USER_LDAP_REFUSED_COLLISION");

        // Local user untouched.
        var localAfter = await _db.Users.SingleAsync(u => u.Id == local.Id);
        localAfter.Provider.Should().Be(AuthProvider.Local);
        localAfter.PasswordHash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task UsernameCollisionWithLocalAdmin_PasswordHashSet_Refuses()
    {
        // The colliding local user has a real password and Admin role — merging would grant
        // the LDAP user the existing local user's history and privileges.
        var local = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice@firma.de",
            Provider = AuthProvider.Local,
            PasswordHash = "$2a$12$irrelevanthash",
            Role = UserRole.Admin,
        };
        _db.Users.Add(local);
        await _db.SaveChangesAsync();

        var mapper = NewMapper(DefaultOptions());
        var outcome = await mapper.MapAsync(Sample(), default);

        outcome.Result.Should().Be(ExternalUserMapResult.RefusedUsernameCollision);
        _audit.Calls.Should().ContainSingle(c => c.Action == "USER_LDAP_REFUSED_COLLISION");
    }

    [Fact]
    public async Task UsernameCollisionWithPasswordlessLocalUser_Refuses()
    {
        // Even a pre-staged passwordless local row is never merged automatically — the
        // operator must resolve the identity migration explicitly.
        var local = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice@firma.de",
            Provider = AuthProvider.Local,
            PasswordHash = null,
            Role = UserRole.Operator,
            SecurityStamp = 4,
        };
        _db.Users.Add(local);
        await _db.SaveChangesAsync();

        var sid = "S-1-5-21-1-1-1-512";
        var mapper = NewMapper(DefaultOptions(mappings: (sid, UserRole.Admin)));
        var outcome = await mapper.MapAsync(Sample(groupSids: sid), default);

        outcome.Result.Should().Be(ExternalUserMapResult.RefusedUsernameCollision);
        outcome.User.Should().BeNull();
        local.Provider.Should().Be(AuthProvider.Local);
        local.ExternalId.Should().BeNull();
        local.SecurityStamp.Should().Be(4);
        _audit.Calls.Should().ContainSingle(c => c.Action == "USER_LDAP_REFUSED_COLLISION");
    }

    [Fact]
    public async Task UsernameCollisionWithSoleActiveLocalAdmin_IsRefusedWithoutMutation()
    {
        const string oldSid = "S-1-5-21-1-1-1-512";
        var local = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice@firma.de",
            Provider = AuthProvider.Local,
            PasswordHash = null,
            KnownGroupSidsJson = $"[\"{oldSid}\"]",
            Role = UserRole.Admin,
            IsActive = true,
            SecurityStamp = 3,
        };
        _db.Users.Add(local);
        await _db.SaveChangesAsync();

        var mapper = NewMapper(DefaultOptions());
        var outcome = await mapper.MapAsync(Sample(groupSids: "S-1-5-21-1-1-1-513"), default);

        outcome.Result.Should().Be(ExternalUserMapResult.RefusedUsernameCollision);
        outcome.User.Should().BeNull();

        var persisted = await _db.Users.AsNoTracking().SingleAsync(u => u.Id == local.Id);
        persisted.Provider.Should().Be(AuthProvider.Local);
        persisted.ExternalId.Should().BeNull();
        persisted.Role.Should().Be(UserRole.Admin);
        persisted.SecurityStamp.Should().Be(3);
        _audit.Calls.Should().ContainSingle(c => c.Action == "USER_LDAP_REFUSED_COLLISION");
    }

    [Fact]
    public async Task GroupSidsList_RoundTripsAsJsonArray()
    {
        await SeedExistingAdminAsync();
        var mapper = NewMapper();
        var outcome = await mapper.MapAsync(
            new LdapAuthResult(
                "guid-aaa",
                "alice@firma.de",
                "Alice Example",
                new[] { "S-1-5-21-1-1-1-512", "S-1-5-21-1-1-1-513", DefaultAllowedGroup }),
            default);

        outcome.User!.KnownGroupSidsJson.Should().Be(
            "[\"S-1-5-21-1-1-1-512\",\"S-1-5-21-1-1-1-513\",\"S-1-5-21-1-1-1-9999\"]");
    }

    [Fact]
    public async Task LdapThenWindows_WithSameAdSid_ResolvesToOneUser()
    {
        await SeedExistingAdminAsync();
        const string sid = "S-1-5-21-111-222-333-1001";
        const string objectGuid = "7930fd29-efaf-4c92-a3f0-e59ab2875726";
        var mapper = NewMapper();

        var ldap = await mapper.MapAsync(
            new LdapAuthResult(sid, "alice@firma.de", "Alice", [DefaultAllowedGroup], objectGuid),
            AuthProvider.Ldap,
            default);
        _db.ChangeTracker.Clear();
        var windows = await mapper.MapAsync(
            new LdapAuthResult(sid, @"FIRMA\alice", "Alice", [DefaultAllowedGroup]),
            AuthProvider.Windows,
            default);

        ldap.Result.Should().Be(ExternalUserMapResult.Mapped);
        windows.Result.Should().Be(ExternalUserMapResult.Mapped);
        windows.User!.Id.Should().Be(ldap.User!.Id);
        (await _db.Users.CountAsync()).Should().Be(2, "only the seeded admin and Alice may exist");
        (await _db.ExternalIdentities.CountAsync()).Should().Be(1);
        var identity = await _db.ExternalIdentities.SingleAsync();
        identity.Authority.Should().Be(ExternalIdentity.ActiveDirectoryAuthority);
        identity.Subject.Should().Be(sid);
    }

    [Fact]
    public async Task ConcurrentJit_ForSameCanonicalIdentity_CreatesOneUser()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"nodepilot-identity-jit-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        try
        {
            await using (var seed = new NodePilotDbContext(options))
            {
                await seed.Database.EnsureCreatedAsync();
                seed.Users.Add(new User
                {
                    Id = Guid.NewGuid(), Username = "admin", Provider = AuthProvider.Local,
                    PasswordHash = "hash", Role = UserRole.Admin, IsActive = true, IsBreakGlass = true,
                });
                await seed.SaveChangesAsync();
            }

            await using var dbA = new NodePilotDbContext(options);
            await using var dbB = new NodePilotDbContext(options);
            var monitor = new StaticOptionsMonitor<LdapOptions>(DefaultOptions());
            var mapperA = new ExternalUserMapper(
                dbA, monitor, new CapturingAuditWriter(), new MemoryCache(new MemoryCacheOptions()),
                NullLogger<ExternalUserMapper>.Instance);
            var mapperB = new ExternalUserMapper(
                dbB, monitor, new CapturingAuditWriter(), new MemoryCache(new MemoryCacheOptions()),
                NullLogger<ExternalUserMapper>.Instance);
            var identity = new LdapAuthResult(
                "S-1-5-21-111-222-333-1001", "alice@firma.de", "Alice",
                [DefaultAllowedGroup], "7930fd29-efaf-4c92-a3f0-e59ab2875726");

            var results = await Task.WhenAll(
                mapperA.MapAsync(identity, AuthProvider.Ldap, default),
                mapperB.MapAsync(identity, AuthProvider.Ldap, default));

            results.Should().OnlyContain(r => r.Result == ExternalUserMapResult.Mapped);
            results.Select(r => r.User!.Id).Distinct().Should().ContainSingle();
            await using var verify = new NodePilotDbContext(options);
            (await verify.Users.CountAsync()).Should().Be(2);
            (await verify.ExternalIdentities.CountAsync()).Should().Be(1);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task LegacyLdapAndCanonicalWindowsRows_ForSamePerson_AreNotMerged()
    {
        const string sid = "S-1-5-21-111-222-333-1001";
        const string objectGuid = "7930fd29-efaf-4c92-a3f0-e59ab2875726";
        var ldapUser = new User
        {
            Id = Guid.NewGuid(), Username = "alice@firma.de", Provider = AuthProvider.Ldap,
            ExternalId = objectGuid, Role = UserRole.Viewer, IsActive = true,
        };
        var windowsUser = new User
        {
            Id = Guid.NewGuid(), Username = @"FIRMA\alice", Provider = AuthProvider.Windows,
            ExternalId = sid, Role = UserRole.Viewer, IsActive = true,
        };
        _db.Users.AddRange(ldapUser, windowsUser);
        _db.ExternalIdentities.Add(new ExternalIdentity
        {
            Id = Guid.NewGuid(), UserId = windowsUser.Id,
            Authority = ExternalIdentity.ActiveDirectoryAuthority, Subject = sid,
        });
        await _db.SaveChangesAsync();

        var outcome = await NewMapper().MapAsync(
            new LdapAuthResult(sid, ldapUser.Username, "Alice", [DefaultAllowedGroup], objectGuid),
            AuthProvider.Ldap,
            default);

        outcome.Result.Should().Be(ExternalUserMapResult.RefusedIdentityConflict);
        outcome.User.Should().BeNull();
        (await _db.Users.CountAsync()).Should().Be(2);
        (await _db.ExternalIdentities.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task UniqueAuthorityAndSubject_IsEnforcedByDatabase()
    {
        var first = new User { Id = Guid.NewGuid(), Username = "first", Provider = AuthProvider.Windows };
        var second = new User { Id = Guid.NewGuid(), Username = "second", Provider = AuthProvider.Windows };
        _db.Users.AddRange(first, second);
        _db.ExternalIdentities.AddRange(
            new ExternalIdentity
            {
                Id = Guid.NewGuid(), UserId = first.Id,
                Authority = ExternalIdentity.ActiveDirectoryAuthority, Subject = "S-1-5-21-1-2-3-1001",
            },
            new ExternalIdentity
            {
                Id = Guid.NewGuid(), UserId = second.Id,
                Authority = ExternalIdentity.ActiveDirectoryAuthority, Subject = "S-1-5-21-1-2-3-1001",
            });

        var save = async () => await _db.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task TombstonedCanonicalIdentity_CannotBeJitRecreated()
    {
        var tombstone = new User
        {
            Id = Guid.NewGuid(), Username = "deleted-alice", Provider = AuthProvider.Ldap,
            ExternalId = "S-1-5-21-1-2-3-1001", IsActive = false, IsTombstoned = true,
        };
        _db.Users.Add(tombstone);
        _db.ExternalIdentities.Add(new ExternalIdentity
        {
            Id = Guid.NewGuid(), UserId = tombstone.Id,
            Authority = ExternalIdentity.ActiveDirectoryAuthority,
            Subject = tombstone.ExternalId,
        });
        await _db.SaveChangesAsync();

        var outcome = await NewMapper().MapAsync(
            new LdapAuthResult(tombstone.ExternalId, "alice@firma.de", "Alice", [DefaultAllowedGroup]),
            default);

        outcome.Result.Should().Be(ExternalUserMapResult.RefusedTombstoned);
        outcome.User.Should().BeNull();
        (await _db.Users.CountAsync()).Should().Be(1);
    }

    private sealed class ThrowingAuditStager : IAuditStager
    {
        public AuditLogEntry Build(
            string action,
            AuditActor actor,
            string? resourceType = null,
            Guid? resourceId = null,
            string? details = null) =>
            throw new InvalidOperationException("audit staging failed");
    }

    private sealed class BlockingAuditWriter : IAuditWriter
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public async Task LogAsync(
            string action,
            string? resourceType = null,
            Guid? resourceId = null,
            string? details = null,
            CancellationToken ct = default)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(ct);
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class CancelOnFirstLogLogger<T>(CancellationTokenSource cancellation) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => cancellation.Cancel();
    }

}
