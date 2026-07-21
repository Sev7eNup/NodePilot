using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Api.Security;
using NodePilot.Api.Security.Ldap;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Security.Ldap;

public sealed class DirectorySynchronizationServiceTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly NodePilotDbContext _db;

    public DirectorySynchronizationServiceTests()
    {
        (_connection, _db) = TestDbFactory.CreateWithConnection();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Sync_GroupAndRoleChange_RevokesSessionsAndReplacesMemberships()
    {
        const string subject = "S-1-5-21-1-2-3-1001";
        const string oldGroup = "S-1-5-21-1-2-3-2001";
        const string newGroup = "S-1-5-21-1-2-3-2002";
        var user = ExternalUser(subject, UserRole.Admin);
        var session = ActiveSession(user);
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "scheduled", DefinitionJson = "{}" };
        var pendingExecution = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, TriggeredBy = "scheduleTrigger",
            StartedByUserId = user.Id, Status = ExecutionStatus.Pending, StartedAt = DateTime.UtcNow,
        };
        _db.AddRange(user, new ExternalIdentity
        {
            Id = Guid.NewGuid(), UserId = user.Id,
            Authority = ExternalIdentity.ActiveDirectoryAuthority, Subject = subject,
        }, new DirectoryMembership { UserId = user.Id, GroupKey = oldGroup }, session,
            workflow, pendingExecution);
        await _db.SaveChangesAsync();
        var adapter = new FakeAdapter
        {
            Snapshot = new LdapDirectorySnapshot(subject, true, user.Username, user.Username, [newGroup]),
        };
        var options = Options(newGroup, (newGroup, UserRole.Operator));

        await Service(adapter, options).SyncOnceAsync(default);

        _db.ChangeTracker.Clear();
        var persisted = await _db.Users.SingleAsync(u => u.Id == user.Id);
        persisted.Role.Should().Be(UserRole.Operator);
        persisted.SecurityStamp.Should().Be(1);
        persisted.DirectorySyncStatus.Should().Be("Current");
        (await _db.DirectoryMemberships.Select(m => m.GroupKey).ToListAsync())
            .Should().Equal(newGroup);
        (await _db.AuthSessions.SingleAsync(s => s.Id == session.Id)).RevokedAt.Should().NotBeNull();
        (await _db.WorkflowExecutions.SingleAsync(e => e.Id == pendingExecution.Id)).Should().Match<WorkflowExecution>(
            execution => execution.Status == ExecutionStatus.Cancelled
                         && execution.CancelledBy == "directory-offboarding");
    }

    [Fact]
    public async Task Sync_MissingDirectoryObject_TombstonesAndDeactivatesUser()
    {
        const string subject = "S-1-5-21-1-2-3-1001";
        const string canarySubject = "S-1-5-21-1-2-3-1002";
        var user = ExternalUser(subject, UserRole.Operator);
        var canary = ExternalUser(canarySubject, UserRole.Viewer);
        canary.Username = "canary@example.test";
        _db.AddRange(user, canary, new ExternalIdentity
        {
            Id = Guid.NewGuid(), UserId = user.Id,
            Authority = ExternalIdentity.ActiveDirectoryAuthority, Subject = subject,
        }, new ExternalIdentity
        {
            Id = Guid.NewGuid(), UserId = canary.Id,
            Authority = ExternalIdentity.ActiveDirectoryAuthority, Subject = canarySubject,
        });
        await _db.SaveChangesAsync();

        await Service(new FakeAdapter
            {
                Snapshots = new Dictionary<string, LdapDirectorySnapshot?>
                {
                    [subject] = null,
                    [canarySubject] = new LdapDirectorySnapshot(
                        canarySubject, true, canary.Username, canary.Username,
                        ["S-1-5-21-1-2-3-2001"]),
                },
            }, Options("S-1-5-21-1-2-3-2001"))
            .SyncOnceAsync(default);

        _db.ChangeTracker.Clear();
        var persisted = await _db.Users.SingleAsync(u => u.Id == user.Id);
        persisted.IsActive.Should().BeFalse();
        persisted.IsTombstoned.Should().BeTrue();
        persisted.DirectorySyncStatus.Should().Be("Missing");
    }

    [Fact]
    public async Task Sync_AllKnownObjectsNotFound_FailsClosedWithoutTombstoning()
    {
        const string subject = "S-1-5-21-1-2-3-1001";
        var previous = DateTime.UtcNow.AddMinutes(-10);
        var user = ExternalUser(subject, UserRole.Operator);
        user.LastDirectorySyncAt = previous;
        _db.AddRange(user, new ExternalIdentity
        {
            Id = Guid.NewGuid(), UserId = user.Id,
            Authority = ExternalIdentity.ActiveDirectoryAuthority, Subject = subject,
        });
        await _db.SaveChangesAsync();

        await Service(new FakeAdapter { Snapshot = null }, Options("S-1-5-21-1-2-3-2001"))
            .SyncOnceAsync(default);

        _db.ChangeTracker.Clear();
        var persisted = await _db.Users.SingleAsync(candidate => candidate.Id == user.Id);
        persisted.IsActive.Should().BeTrue();
        persisted.IsTombstoned.Should().BeFalse();
        persisted.LastDirectorySyncAt.Should().Be(previous);
        persisted.DirectorySyncStatus.Should().Be("Failed");
    }

    [Fact]
    public async Task Sync_InfrastructureFailure_DoesNotRefreshFreshnessTimestamp()
    {
        const string subject = "S-1-5-21-1-2-3-1001";
        var previous = DateTime.UtcNow.AddMinutes(-10);
        var user = ExternalUser(subject, UserRole.Operator);
        user.LastDirectorySyncAt = previous;
        _db.AddRange(user, new ExternalIdentity
        {
            Id = Guid.NewGuid(), UserId = user.Id,
            Authority = ExternalIdentity.ActiveDirectoryAuthority, Subject = subject,
        });
        await _db.SaveChangesAsync();

        await Service(new FakeAdapter { ThrowInfrastructure = true }, Options("S-1-5-21-1-2-3-2001"))
            .SyncOnceAsync(default);

        _db.ChangeTracker.Clear();
        var persisted = await _db.Users.SingleAsync(u => u.Id == user.Id);
        persisted.LastDirectorySyncAt.Should().Be(previous);
        persisted.DirectorySyncStatus.Should().Be("Failed");
    }

    [Fact]
    public async Task Sync_HealthyDirectory_DoesNotUndoExplicitLocalSuspension()
    {
        const string subject = "S-1-5-21-1-2-3-1001";
        const string allowedGroup = "S-1-5-21-1-2-3-2001";
        var user = ExternalUser(subject, UserRole.Operator);
        user.IsActive = false;
        _db.AddRange(user, new ExternalIdentity
        {
            Id = Guid.NewGuid(), UserId = user.Id,
            Authority = ExternalIdentity.ActiveDirectoryAuthority, Subject = subject,
        });
        await _db.SaveChangesAsync();
        var adapter = new FakeAdapter
        {
            Snapshot = new LdapDirectorySnapshot(
                subject, true, user.Username, user.Username, [allowedGroup]),
        };

        await Service(adapter, Options(allowedGroup)).SyncOnceAsync(default);

        _db.ChangeTracker.Clear();
        var persisted = await _db.Users.SingleAsync(u => u.Id == user.Id);
        persisted.IsActive.Should().BeFalse();
        persisted.DirectorySyncStatus.Should().Be("LocallyDisabled");
    }

    [Fact]
    public async Task Sync_LeaseHandoffDuringDirectoryIo_DoesNotCommitOldLeaderSnapshot()
    {
        const string subject = "S-1-5-21-1-2-3-1001";
        const string group = "S-1-5-21-1-2-3-2001";
        var user = ExternalUser(subject, UserRole.Admin);
        _db.AddRange(user, new ExternalIdentity
        {
            Id = Guid.NewGuid(), UserId = user.Id,
            Authority = ExternalIdentity.ActiveDirectoryAuthority, Subject = subject,
        });
        await _db.SaveChangesAsync();
        var cluster = new MutableCluster { IsLeader = true, LeaseEpoch = 7 };
        var adapter = new FakeAdapter
        {
            Snapshot = new LdapDirectorySnapshot(subject, true, user.Username, user.Username, [group]),
            OnLookup = () => { cluster.IsLeader = false; cluster.LeaseEpoch = 8; },
        };

        (await Service(adapter, Options(group), cluster).SyncOnceAsync(default)).Should().Be(0);

        _db.ChangeTracker.Clear();
        var persisted = await _db.Users.SingleAsync(x => x.Id == user.Id);
        persisted.SecurityStamp.Should().Be(0);
        (await _db.DirectoryMemberships.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Sync_DbLeaseHandoffWithStaleInMemoryLeader_DoesNotCommitSnapshot()
    {
        const string subject = "S-1-5-21-1-2-3-1001";
        const string group = "S-1-5-21-1-2-3-2001";
        var user = ExternalUser(subject, UserRole.Admin);
        var previousSync = user.LastDirectorySyncAt;
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "stale leader", DefinitionJson = "{}" };
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, TriggeredBy = "scheduleTrigger",
            StartedByUserId = user.Id, Status = ExecutionStatus.Running, StartedAt = DateTime.UtcNow,
        };
        _db.AddRange(user, new ExternalIdentity
        {
            Id = Guid.NewGuid(), UserId = user.Id,
            Authority = ExternalIdentity.ActiveDirectoryAuthority, Subject = subject,
        }, workflow, execution);
        await _db.SaveChangesAsync();
        var staleCluster = new MutableCluster { IsLeader = true, LeaseEpoch = 7 };
        var adapter = new FakeAdapter
        {
            Snapshot = new LdapDirectorySnapshot(
                subject, true, user.Username, user.Username, [group]),
            OnLookup = () => _db.ClusterLeaders
                .Where(lease => lease.Resource == "primary")
                .ExecuteUpdate(setters => setters
                    .SetProperty(lease => lease.OwnerNodeId, "node-b")
                    .SetProperty(lease => lease.LeaseEpoch, 8L)
                    .SetProperty(lease => lease.ExpiresAt, DateTime.UtcNow.AddMinutes(1))),
        };

        (await Service(
                adapter, Options(group, (group, UserRole.Operator)), staleCluster)
            .SyncOnceAsync(default))
            .Should().Be(0);

        _db.ChangeTracker.Clear();
        var persisted = await _db.Users.SingleAsync(candidate => candidate.Id == user.Id);
        persisted.SecurityStamp.Should().Be(0);
        persisted.LastDirectorySyncAt.Should().Be(previousSync);
        (await _db.DirectoryMemberships.CountAsync()).Should().Be(0);
        (await _db.WorkflowExecutions.SingleAsync(candidate => candidate.Id == execution.Id))
            .Status.Should().Be(ExecutionStatus.Running);
        (await _db.AuditLog.CountAsync()).Should().Be(0);
        var lease = await _db.ClusterLeaders.SingleAsync(candidate => candidate.Resource == "primary");
        lease.OwnerNodeId.Should().Be("node-b");
        lease.LeaseEpoch.Should().Be(8);
    }

    [Fact]
    public async Task Sync_LeaseHandoffAfterCommit_PreservesDurableCancellationAndAudit()
    {
        const string subject = "S-1-5-21-1-2-3-1001";
        const string group = "S-1-5-21-1-2-3-2001";
        var user = ExternalUser(subject, UserRole.Admin);
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "running", DefinitionJson = "{}" };
        var running = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, TriggeredBy = "scheduleTrigger",
            StartedByUserId = user.Id, Status = ExecutionStatus.Running, StartedAt = DateTime.UtcNow,
        };
        _db.AddRange(user, new ExternalIdentity
        {
            Id = Guid.NewGuid(), UserId = user.Id,
            Authority = ExternalIdentity.ActiveDirectoryAuthority, Subject = subject,
        }, workflow, running);
        await _db.SaveChangesAsync();

        var cluster = new MutableCluster { IsLeader = true, LeaseEpoch = 7 };
        var engine = new Mock<IWorkflowEngine>();
        engine.Setup(candidate => candidate.CancelAsync(
                running.Id, "directory-offboarding", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cluster.IsLeader = false;
                cluster.LeaseEpoch = 8;
                return Task.FromResult(false);
            });
        var adapter = new FakeAdapter
        {
            Snapshot = new LdapDirectorySnapshot(subject, true, user.Username, user.Username, [group]),
        };

        (await Service(
                adapter,
                Options(group, (group, UserRole.Operator)),
                cluster,
                engine.Object)
            .SyncOnceAsync(default)).Should().Be(1);

        _db.ChangeTracker.Clear();
        (await _db.WorkflowExecutions.SingleAsync(candidate => candidate.Id == running.Id))
            .Status.Should().Be(ExecutionStatus.Cancelled);
        (await _db.AuditLog.CountAsync(entry =>
            entry.Action == AuditActions.UserDirectorySynced)).Should().Be(1);
    }

    [Fact]
    public async Task Sync_AuditStagingFailure_RollsBackSnapshotSessionAndExecutionTogether()
    {
        const string subject = "S-1-5-21-1-2-3-1001";
        const string oldGroup = "S-1-5-21-1-2-3-2001";
        const string newGroup = "S-1-5-21-1-2-3-2002";
        var user = ExternalUser(subject, UserRole.Admin);
        user.KnownGroupSidsJson = $"[\"{oldGroup}\"]";
        var previousSync = user.LastDirectorySyncAt;
        var session = ActiveSession(user);
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "atomic rollback", DefinitionJson = "{}" };
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, TriggeredBy = "scheduleTrigger",
            StartedByUserId = user.Id, Status = ExecutionStatus.Running, StartedAt = DateTime.UtcNow,
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
                GroupKey = oldGroup, LastSeenAt = DateTime.UtcNow,
            },
            session, workflow, execution);
        await _db.SaveChangesAsync();
        var adapter = new FakeAdapter
        {
            Snapshot = new LdapDirectorySnapshot(
                subject, true, user.Username, user.Username, [newGroup]),
        };
        var engine = new Mock<IWorkflowEngine>();
        engine.Setup(candidate => candidate.CancelAsync(
                execution.Id, "directory-offboarding", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sync = () => Service(
                adapter,
                Options(newGroup, (newGroup, UserRole.Operator)),
                engine: engine.Object,
                auditStager: new ThrowingAuditStager())
            .SyncOnceAsync(default);

        await sync.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("audit staging failed");
        _db.ChangeTracker.Clear();
        var persisted = await _db.Users.SingleAsync(candidate => candidate.Id == user.Id);
        persisted.Role.Should().Be(UserRole.Admin);
        persisted.SecurityStamp.Should().Be(0);
        persisted.LastDirectorySyncAt.Should().Be(previousSync);
        persisted.KnownGroupSidsJson.Should().Be($"[\"{oldGroup}\"]");
        (await _db.DirectoryMemberships.SingleAsync(candidate => candidate.UserId == user.Id))
            .GroupKey.Should().Be(oldGroup);
        (await _db.AuthSessions.SingleAsync(candidate => candidate.Id == session.Id))
            .RevokedAt.Should().BeNull();
        (await _db.WorkflowExecutions.SingleAsync(candidate => candidate.Id == execution.Id))
            .Status.Should().Be(ExecutionStatus.Running);
        (await _db.AuditLog.CountAsync()).Should().Be(0);
        engine.Verify(candidate => candidate.CancelAsync(
            It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        (await Service(
                adapter,
                Options(newGroup, (newGroup, UserRole.Operator)),
                engine: engine.Object)
            .SyncOnceAsync(default)).Should().Be(1);

        _db.ChangeTracker.Clear();
        persisted = await _db.Users.SingleAsync(candidate => candidate.Id == user.Id);
        persisted.Role.Should().Be(UserRole.Operator);
        persisted.SecurityStamp.Should().Be(1);
        persisted.KnownGroupSidsJson.Should().Be($"[\"{newGroup}\"]");
        (await _db.DirectoryMemberships.SingleAsync(candidate => candidate.UserId == user.Id))
            .GroupKey.Should().Be(newGroup);
        (await _db.AuthSessions.SingleAsync(candidate => candidate.Id == session.Id))
            .RevokedAt.Should().NotBeNull();
        (await _db.WorkflowExecutions.SingleAsync(candidate => candidate.Id == execution.Id))
            .Status.Should().Be(ExecutionStatus.Cancelled);
        (await _db.AuditLog.CountAsync(entry =>
            entry.Action == AuditActions.UserDirectorySynced)).Should().Be(1);
        engine.Verify(candidate => candidate.CancelAsync(
            execution.Id, "directory-offboarding", It.IsAny<CancellationToken>()), Times.Once);
    }

    private DirectorySynchronizationService Service(
        FakeAdapter adapter,
        LdapOptions options,
        IClusterStateProvider? clusterState = null,
        IWorkflowEngine? engine = null,
        IAuditWriter? audit = null,
        IAuditStager? auditStager = null)
    {
        var effectiveCluster = clusterState ?? new MutableCluster { IsLeader = true };
        if (effectiveCluster.LeaseEpoch > 0
            && !_db.ClusterLeaders.Any(lease => lease.Resource == "primary"))
        {
            var now = DateTime.UtcNow;
            _db.ClusterLeaders.Add(new ClusterLeader
            {
                Resource = "primary",
                OwnerNodeId = effectiveCluster.NodeId,
                LeaseEpoch = effectiveCluster.LeaseEpoch,
                AcquiredAt = now,
                LastRenewedAt = now,
                ExpiresAt = now.AddMinutes(1),
            });
            _db.SaveChanges();
        }
        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton<ILdapConnectionAdapter>(adapter);
        services.AddSingleton(audit ?? NoopAuditWriter.Instance);
        services.AddSingleton(auditStager ?? new AuditStager());
        if (engine is not null) services.AddSingleton(engine);
        services.AddMemoryCache();
        var provider = services.BuildServiceProvider();
        return new DirectorySynchronizationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<LdapOptions>(options),
            effectiveCluster,
            NullLogger<DirectorySynchronizationService>.Instance);
    }

    private static LdapOptions Options(
        string allowedGroup,
        params (string Group, UserRole Role)[] mappings) => new()
    {
        Enabled = true,
        AllowedGroupSids = [allowedGroup],
        GlobalRoleMappings = mappings.Select(m => new GlobalRoleMapping
        {
            GroupSid = m.Group,
            Role = m.Role,
        }).ToList(),
    };

    private static User ExternalUser(string subject, UserRole role) => new()
    {
        Id = Guid.NewGuid(), Username = "alice@example.test", Provider = AuthProvider.Ldap,
        ExternalId = subject, Role = role, IsActive = true,
        LastDirectorySyncAt = DateTime.UtcNow, DirectorySyncStatus = "Current",
    };

    private static AuthSession ActiveSession(User user) => new()
    {
        Id = Guid.NewGuid(), UserId = user.Id, AuthenticationMethod = "Ldap",
        CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddHours(1), AuthorizationVersion = user.SecurityStamp,
        CurrentJti = Guid.NewGuid().ToString("N"),
    };

    private sealed class FakeAdapter : ILdapConnectionAdapter
    {
        public LdapDirectorySnapshot? Snapshot { get; init; }
        public IReadOnlyDictionary<string, LdapDirectorySnapshot?>? Snapshots { get; init; }
        public bool ThrowInfrastructure { get; init; }
        public Action? OnLookup { get; init; }
        public Task<LdapAuthResult?> AuthenticateAsync(string upn, string password, CancellationToken ct) =>
            Task.FromResult<LdapAuthResult?>(null);
        public Task<LdapDirectorySnapshot?> LookupBySubjectAsync(string subject, CancellationToken ct)
        {
            OnLookup?.Invoke();
            if (ThrowInfrastructure) throw new LdapInfrastructureException("offline");
            return Task.FromResult(
                Snapshots is not null && Snapshots.TryGetValue(subject, out var snapshot)
                    ? snapshot
                    : Snapshot);
        }
    }

    private sealed class MutableCluster : IClusterStateProvider
    {
        public bool IsLeader { get; set; }
        public string NodeId => "test";
        public DateTime? LeaseExpiresAt => null;
        public long LeaseEpoch { get; set; }
        public DateTime? LastSuccessfulRenewAt => null;
        public event Action<long>? OnLeadershipAcquired { add { } remove { } }
        public event Action? OnLeadershipLost { add { } remove { } }
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
}
