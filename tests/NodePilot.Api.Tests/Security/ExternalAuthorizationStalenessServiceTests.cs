using FluentAssertions;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NodePilot.Api.Security;
using NodePilot.Api.Security.Oidc;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using NodePilot.Api.Tests.TestSupport;
using Xunit;

namespace NodePilot.Api.Tests.Security;

public sealed class ExternalAuthorizationStalenessServiceTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly NodePilotDbContext _db;

    public ExternalAuthorizationStalenessServiceTests()
        => (_connection, _db) = TestDbFactory.CreateWithConnection();

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Sweep_RevokesSessionAndCancelsPendingRunningAndPausedExecutions()
    {
        var now = DateTime.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(), Username = "alice@example.test", Provider = AuthProvider.Ldap,
            Role = UserRole.Operator, IsActive = true, LastDirectorySyncAt = now.AddMinutes(-15),
            DirectorySyncStatus = "Failed",
        };
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "external", DefinitionJson = "{}" };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(), UserId = user.Id, AuthenticationMethod = "Ldap",
            CreatedAt = now.AddHours(-1), LastSeenAt = now.AddMinutes(-1),
            ExpiresAt = now.AddHours(1), CurrentJti = Guid.NewGuid().ToString("N"),
        };
        var pending = Execution(workflow, user, ExecutionStatus.Pending);
        var running = Execution(workflow, user, ExecutionStatus.Running);
        var paused = Execution(workflow, user, ExecutionStatus.Paused);
        _db.AddRange(user, workflow, session, pending, running, paused);
        await _db.SaveChangesAsync();

        var engine = new Mock<IWorkflowEngine>();
        engine.Setup(x => x.CancelAsync(It.IsAny<Guid>(), "authorization-stale", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton(engine.Object);
        services.AddSingleton<IAuditWriter>(NoopAuditWriter.Instance);
        services.AddSingleton(Options.Create(new AuthenticationPolicyOptions
        {
            MaxAuthorizationStalenessMinutes = 15,
        }));
        services.AddSingleton(Options.Create(new EnterpriseOidcOptions()));
        services.AddScoped<ExternalAuthorizationEvaluator>();
        services.AddMemoryCache();
        using var provider = services.BuildServiceProvider();
        var cluster = new Mock<IClusterStateProvider>();
        cluster.SetupGet(x => x.IsLeader).Returns(true);
        var service = new ExternalAuthorizationStalenessService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AuthenticationPolicyOptions
            {
                MaxAuthorizationStalenessMinutes = 15,
            }),
            cluster.Object,
            NullLogger<ExternalAuthorizationStalenessService>.Instance,
            NodePilot.TestCommons.TestDatabaseAvailability.Available);

        (await service.SweepOnceAsync(now, default)).Should().Be(1);

        _db.ChangeTracker.Clear();
        var persistedUser = await _db.Users.SingleAsync(x => x.Id == user.Id);
        persistedUser.DirectorySyncStatus.Should().Be("Stale");
        persistedUser.SecurityStamp.Should().Be(1);
        (await _db.AuthSessions.SingleAsync(x => x.Id == session.Id)).RevokedAt.Should().Be(now);
        (await _db.WorkflowExecutions.SingleAsync(x => x.Id == pending.Id)).Should()
            .Match<WorkflowExecution>(x => x.Status == ExecutionStatus.Cancelled
                                          && x.CancelledBy == "authorization-stale");
        (await _db.WorkflowExecutions.SingleAsync(x => x.Id == running.Id)).Status
            .Should().Be(ExecutionStatus.Cancelled);
        (await _db.WorkflowExecutions.SingleAsync(x => x.Id == paused.Id)).Status
            .Should().Be(ExecutionStatus.Cancelled);
        engine.Verify(x => x.CancelAsync(running.Id, "authorization-stale", It.IsAny<CancellationToken>()), Times.Once);
        engine.Verify(x => x.CancelAsync(paused.Id, "authorization-stale", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Sweep_CancelsWorkForInactiveAndTombstonedExternalPrincipals()
    {
        var now = DateTime.UtcNow;
        var inactive = new User
        {
            Id = Guid.NewGuid(), Username = "inactive@example.test", Provider = AuthProvider.Ldap,
            Role = UserRole.Operator, IsActive = false, LastDirectorySyncAt = now,
            DirectorySyncStatus = "Disabled",
        };
        var tombstoned = new User
        {
            Id = Guid.NewGuid(), Username = "deleted@example.test", Provider = AuthProvider.Oidc,
            Role = UserRole.Operator, IsActive = false, IsTombstoned = true,
            LastDirectorySyncAt = now, DirectorySyncStatus = "Tombstoned",
        };
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "external", DefinitionJson = "{}" };
        var pending = Execution(workflow, inactive, ExecutionStatus.Pending);
        var running = Execution(workflow, tombstoned, ExecutionStatus.Running);
        var inactiveSession = new AuthSession
        {
            Id = Guid.NewGuid(), UserId = inactive.Id, AuthenticationMethod = "Ldap",
            CreatedAt = now.AddHours(-1), LastSeenAt = now,
            ExpiresAt = now.AddHours(1), CurrentJti = Guid.NewGuid().ToString("N"),
        };
        var tombstonedSession = new AuthSession
        {
            Id = Guid.NewGuid(), UserId = tombstoned.Id, AuthenticationMethod = "Oidc",
            CreatedAt = now.AddHours(-1), LastSeenAt = now,
            ExpiresAt = now.AddHours(1), CurrentJti = Guid.NewGuid().ToString("N"),
        };
        _db.AddRange(inactive, tombstoned, workflow, pending, running, inactiveSession, tombstonedSession);
        await _db.SaveChangesAsync();

        var engine = new Mock<IWorkflowEngine>();
        engine.Setup(candidate => candidate.CancelAsync(
                running.Id, "authorization-stale", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton(engine.Object);
        services.AddSingleton<IAuditWriter>(NoopAuditWriter.Instance);
        services.AddSingleton(Options.Create(new AuthenticationPolicyOptions
        {
            MaxAuthorizationStalenessMinutes = 15,
        }));
        services.AddSingleton(Options.Create(new EnterpriseOidcOptions()));
        services.AddScoped<ExternalAuthorizationEvaluator>();
        services.AddMemoryCache();
        using var provider = services.BuildServiceProvider();
        var cluster = new Mock<IClusterStateProvider>();
        cluster.SetupGet(candidate => candidate.IsLeader).Returns(true);
        var service = new ExternalAuthorizationStalenessService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AuthenticationPolicyOptions
            {
                MaxAuthorizationStalenessMinutes = 15,
            }),
            cluster.Object,
            NullLogger<ExternalAuthorizationStalenessService>.Instance,
            NodePilot.TestCommons.TestDatabaseAvailability.Available);

        (await service.SweepOnceAsync(now, default)).Should().Be(2);

        _db.ChangeTracker.Clear();
        (await _db.AuthSessions.SingleAsync(candidate => candidate.Id == inactiveSession.Id))
            .RevokedAt.Should().Be(now);
        (await _db.AuthSessions.SingleAsync(candidate => candidate.Id == tombstonedSession.Id))
            .RevokedAt.Should().Be(now);
        (await _db.WorkflowExecutions.SingleAsync(candidate => candidate.Id == pending.Id))
            .Should().Match<WorkflowExecution>(candidate =>
                candidate.Status == ExecutionStatus.Cancelled
                && candidate.CancelledBy == "authorization-stale");
        engine.Verify(candidate => candidate.CancelAsync(
            running.Id, "authorization-stale", It.IsAny<CancellationToken>()), Times.Once);
        (await _db.Users.SingleAsync(candidate => candidate.Id == inactive.Id))
            .DirectorySyncStatus.Should().Be("Disabled");
        (await _db.Users.SingleAsync(candidate => candidate.Id == tombstoned.Id))
            .DirectorySyncStatus.Should().Be("Tombstoned");
    }

    [Fact]
    public async Task Sweep_DoesNotTouchLocalOrFreshExternalPrincipals()
    {
        var now = DateTime.UtcNow;
        _db.Users.AddRange(
            new User
            {
                Id = Guid.NewGuid(), Username = "local", Provider = AuthProvider.Local,
                Role = UserRole.Admin, IsActive = true, LastDirectorySyncAt = null,
            },
            new User
            {
                Id = Guid.NewGuid(), Username = "fresh", Provider = AuthProvider.Ldap,
                Role = UserRole.Viewer, IsActive = true, LastDirectorySyncAt = now.AddMinutes(-5),
            });
        await _db.SaveChangesAsync();
        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton<IAuditWriter>(NoopAuditWriter.Instance);
        services.AddSingleton(Options.Create(new AuthenticationPolicyOptions()));
        services.AddSingleton(Options.Create(new EnterpriseOidcOptions()));
        services.AddScoped<ExternalAuthorizationEvaluator>();
        services.AddMemoryCache();
        using var provider = services.BuildServiceProvider();
        var service = new ExternalAuthorizationStalenessService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AuthenticationPolicyOptions()),
            Mock.Of<IClusterStateProvider>(),
            NullLogger<ExternalAuthorizationStalenessService>.Instance,
            NodePilot.TestCommons.TestDatabaseAvailability.Available);

        (await service.SweepOnceAsync(now, default)).Should().Be(0);
    }

    [Fact]
    public async Task Sweep_LeaseHandoffAfterDurableCancellation_PreservesCancelledAndSkipsAuditWrites()
    {
        var now = DateTime.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(), Username = "alice@example.test", Provider = AuthProvider.Ldap,
            Role = UserRole.Operator, IsActive = true, LastDirectorySyncAt = now.AddMinutes(-15),
            DirectorySyncStatus = "Failed",
        };
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "external", DefinitionJson = "{}" };
        var running = Execution(workflow, user, ExecutionStatus.Running);
        _db.AddRange(user, workflow, running, new ClusterLeader
        {
            Resource = "primary", OwnerNodeId = "node-a", LeaseEpoch = 7,
            AcquiredAt = now, LastRenewedAt = now, ExpiresAt = now.AddMinutes(1),
        });
        await _db.SaveChangesAsync();

        var isLeader = true;
        long leaseEpoch = 7;
        var cluster = new Mock<IClusterStateProvider>();
        cluster.SetupGet(candidate => candidate.IsLeader).Returns(() => isLeader);
        cluster.SetupGet(candidate => candidate.LeaseEpoch).Returns(() => leaseEpoch);
        cluster.SetupGet(candidate => candidate.NodeId).Returns("node-a");
        var engine = new Mock<IWorkflowEngine>();
        engine.Setup(candidate => candidate.CancelAsync(
                running.Id, "authorization-stale", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                isLeader = false;
                leaseEpoch = 8;
                return Task.FromResult(false);
            });
        var audit = new Mock<IAuditWriter>();
        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton(engine.Object);
        services.AddSingleton(audit.Object);
        services.AddSingleton(Options.Create(new AuthenticationPolicyOptions
        {
            MaxAuthorizationStalenessMinutes = 15,
        }));
        services.AddSingleton(Options.Create(new EnterpriseOidcOptions()));
        services.AddScoped<ExternalAuthorizationEvaluator>();
        services.AddMemoryCache();
        using var provider = services.BuildServiceProvider();
        var service = new ExternalAuthorizationStalenessService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AuthenticationPolicyOptions
            {
                MaxAuthorizationStalenessMinutes = 15,
            }),
            cluster.Object,
            NullLogger<ExternalAuthorizationStalenessService>.Instance,
            NodePilot.TestCommons.TestDatabaseAvailability.Available);

        (await service.SweepOnceAsync(now, default)).Should().Be(1);

        _db.ChangeTracker.Clear();
        (await _db.WorkflowExecutions.SingleAsync(candidate => candidate.Id == running.Id))
            .Status.Should().Be(ExecutionStatus.Cancelled,
                "running work is made durable before the best-effort engine signal");
        audit.Verify(candidate => candidate.LogAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Guid?>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Sweep_DelayedOldLeader_CannotMutateNewLeaderState()
    {
        var now = DateTime.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(), Username = "delayed@example.test", Provider = AuthProvider.Ldap,
            Role = UserRole.Operator, IsActive = true, LastDirectorySyncAt = now.AddMinutes(-15),
            DirectorySyncStatus = "Failed", SecurityStamp = 3,
        };
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "new leader", DefinitionJson = "{}" };
        var running = Execution(workflow, user, ExecutionStatus.Running);
        running.OwnerNodeId = "node-b";
        var session = new AuthSession
        {
            Id = Guid.NewGuid(), UserId = user.Id, AuthenticationMethod = "Ldap",
            CreatedAt = now.AddMinutes(-5), LastSeenAt = now,
            ExpiresAt = now.AddHours(1), CurrentJti = Guid.NewGuid().ToString("N"),
        };
        _db.AddRange(user, workflow, running, session, new ClusterLeader
        {
            Resource = "primary", OwnerNodeId = "node-b", LeaseEpoch = 8,
            AcquiredAt = now, LastRenewedAt = now, ExpiresAt = now.AddMinutes(1),
        });
        await _db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton<IAuditWriter>(NoopAuditWriter.Instance);
        services.AddSingleton(Options.Create(new AuthenticationPolicyOptions
        {
            MaxAuthorizationStalenessMinutes = 15,
        }));
        services.AddSingleton(Options.Create(new EnterpriseOidcOptions()));
        services.AddScoped<ExternalAuthorizationEvaluator>();
        services.AddMemoryCache();
        using var provider = services.BuildServiceProvider();
        var staleCluster = new Mock<IClusterStateProvider>();
        staleCluster.SetupGet(candidate => candidate.IsLeader).Returns(true);
        staleCluster.SetupGet(candidate => candidate.NodeId).Returns("node-a");
        staleCluster.SetupGet(candidate => candidate.LeaseEpoch).Returns(7);
        var service = new ExternalAuthorizationStalenessService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AuthenticationPolicyOptions
            {
                MaxAuthorizationStalenessMinutes = 15,
            }),
            staleCluster.Object,
            NullLogger<ExternalAuthorizationStalenessService>.Instance,
            NodePilot.TestCommons.TestDatabaseAvailability.Available);

        (await service.SweepOnceAsync(now, default)).Should().Be(0);

        _db.ChangeTracker.Clear();
        var persistedUser = await _db.Users.SingleAsync(candidate => candidate.Id == user.Id);
        persistedUser.DirectorySyncStatus.Should().Be("Failed");
        persistedUser.SecurityStamp.Should().Be(3);
        (await _db.AuthSessions.SingleAsync(candidate => candidate.Id == session.Id)).RevokedAt
            .Should().BeNull();
        (await _db.WorkflowExecutions.SingleAsync(candidate => candidate.Id == running.Id)).Status
            .Should().Be(ExecutionStatus.Running);
    }

    private static WorkflowExecution Execution(Workflow workflow, User user, ExecutionStatus status) => new()
    {
        Id = Guid.NewGuid(), WorkflowId = workflow.Id, Workflow = workflow,
        StartedByUserId = user.Id, Status = status, StartedAt = DateTime.UtcNow,
        TriggeredBy = "scheduleTrigger",
    };

    [Fact]
    public async Task Sweep_FreshDirectorySnapshotCommittedAfterInitialVerdict_PreservesCurrentWork()
    {
        var now = DateTime.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(), Username = "refreshed@example.test", Provider = AuthProvider.Ldap,
            Role = UserRole.Operator, IsActive = true, LastDirectorySyncAt = now.AddMinutes(-16),
            DirectorySyncStatus = "Failed", SecurityStamp = 4,
        };
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "fresh principal", DefinitionJson = "{}" };
        var oldSession = new AuthSession
        {
            Id = Guid.NewGuid(), UserId = user.Id, AuthenticationMethod = "Ldap",
            CreatedAt = now.AddHours(-1), LastSeenAt = now.AddMinutes(-16),
            ExpiresAt = now.AddHours(1), CurrentJti = Guid.NewGuid().ToString("N"),
        };
        _db.AddRange(user, workflow, oldSession);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        var freshExecutionId = Guid.NewGuid();
        var refresh = new BeforeSweepTransaction(async () =>
        {
            // A successful directory sync with unchanged rights does not change SecurityStamp.
            await _db.Users.Where(candidate => candidate.Id == user.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.LastDirectorySyncAt, now)
                .SetProperty(candidate => candidate.DirectorySyncStatus, "Success"));
            _db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = freshExecutionId, WorkflowId = workflow.Id, StartedByUserId = user.Id,
                Status = ExecutionStatus.Running, StartedAt = now, TriggeredBy = "manual",
            });
            await _db.SaveChangesAsync();
        });
        await using var sweepDb = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(_connection).AddInterceptors(refresh).Options);
        var engine = new Mock<IWorkflowEngine>();
        engine.Setup(candidate => candidate.CancelAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var services = new ServiceCollection();
        services.AddSingleton(sweepDb);
        services.AddSingleton(engine.Object);
        services.AddSingleton<IAuditWriter>(NoopAuditWriter.Instance);
        services.AddSingleton(Options.Create(new AuthenticationPolicyOptions()));
        services.AddSingleton(Options.Create(new EnterpriseOidcOptions()));
        services.AddScoped<ExternalAuthorizationEvaluator>();
        services.AddMemoryCache();
        using var provider = services.BuildServiceProvider();
        var cluster = new Mock<IClusterStateProvider>();
        cluster.SetupGet(candidate => candidate.IsLeader).Returns(true);
        var service = new ExternalAuthorizationStalenessService(
            provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new AuthenticationPolicyOptions()),
            cluster.Object, NullLogger<ExternalAuthorizationStalenessService>.Instance,
            TestDatabaseAvailability.Available);

        var affected = await service.SweepOnceAsync(now, default);

        refresh.DidRefresh.Should().BeTrue();
        _db.ChangeTracker.Clear();
        using (new FluentAssertions.Execution.AssertionScope())
        {
            affected.Should().Be(0);
            (await _db.Users.SingleAsync(candidate => candidate.Id == user.Id)).SecurityStamp.Should().Be(4);
            (await _db.AuthSessions.SingleAsync(candidate => candidate.Id == oldSession.Id)).RevokedAt.Should().BeNull();
            (await _db.WorkflowExecutions.SingleAsync(candidate => candidate.Id == freshExecutionId)).Status
                .Should().Be(ExecutionStatus.Running);
        }
        engine.Verify(candidate => candidate.CancelAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class BeforeSweepTransaction(Func<Task> refresh) : DbTransactionInterceptor
    {
        public bool DidRefresh { get; private set; }

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            if (!DidRefresh)
            {
                // Finish the competing sync before the sweep acquires its transaction locks.
                // This exercises a real serial order rather than writing through another
                // context on an already locked SQLite connection.
                await refresh();
                DidRefresh = true;
            }
            return result;
        }
    }
}
