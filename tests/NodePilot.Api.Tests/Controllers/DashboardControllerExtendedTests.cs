using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// Coverage for the dashboard extensions added in 2026-05: next-fire times for armed
/// triggers, failing-workflow ranking, queue-depth metrics, edit-lock surfacing, system
/// health heartbeats, and the admin-only audit feed.
/// </summary>
public class DashboardControllerExtendedTests
{
    private static DashboardController NewController(
        NodePilot.Data.NodePilotDbContext db,
        string role = "Admin",
        IClusterStateProvider? cluster = null)
    {
        var controller = new DashboardController(db, new AlwaysAllowAuthorizationService(), cluster);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Role, role) }, "TestAuth"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    private static DashboardStats Unwrap(ActionResult<DashboardStats> result)
        => (DashboardStats)((OkObjectResult)result.Result!).Value!;

    [Fact]
    public async Task Get_WithEnabledScheduleTrigger_ReturnsNextFireUtcAndKindCron()
    {
        var db = TestDbFactory.Create();
        db.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Cron",
            IsEnabled = true,
            UpdatedAt = DateTime.UtcNow,
            DefinitionJson = """{"nodes":[{"id":"t","data":{"activityType":"scheduleTrigger","config":{"cronExpression":"0 0/5 * * * ?"}}}]}""",
            TriggerTypesJson = """["scheduleTrigger"]"""
        });
        await db.SaveChangesAsync();

        var stats = Unwrap(await NewController(db).Get(CancellationToken.None));

        stats.ArmedTriggers.Should().HaveCount(1);
        var trig = stats.ArmedTriggers[0];
        trig.NextFireKind.Should().Be("cron");
        trig.NextFireUtc.Should().NotBeNull();
        trig.NextFireUtc!.Value.Should().BeAfter(DateTime.UtcNow);
        trig.NextFireUtc!.Value.Should().BeBefore(DateTime.UtcNow.AddMinutes(6));
        trig.PollIntervalSeconds.Should().BeNull();
    }

    [Fact]
    public async Task Get_WithFileWatcherTrigger_ReturnsEventDriven()
    {
        var db = TestDbFactory.Create();
        db.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Watcher",
            IsEnabled = true,
            UpdatedAt = DateTime.UtcNow,
            DefinitionJson = """{"nodes":[{"id":"t","data":{"activityType":"fileWatcherTrigger"}}]}""",
            TriggerTypesJson = """["fileWatcherTrigger"]"""
        });
        await db.SaveChangesAsync();

        var stats = Unwrap(await NewController(db).Get(CancellationToken.None));

        var trig = stats.ArmedTriggers.Single();
        trig.NextFireKind.Should().Be("event-driven");
        trig.NextFireUtc.Should().BeNull();
        trig.PollIntervalSeconds.Should().BeNull();
    }

    [Fact]
    public async Task Get_WithDatabaseTrigger_ReturnsPollingWithIntervalSeconds()
    {
        var db = TestDbFactory.Create();
        db.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "DbPoll",
            IsEnabled = true,
            UpdatedAt = DateTime.UtcNow,
            DefinitionJson = """{"nodes":[{"id":"t","data":{"activityType":"databaseTrigger","config":{"intervalSeconds":30}}}]}""",
            TriggerTypesJson = """["databaseTrigger"]"""
        });
        await db.SaveChangesAsync();

        var stats = Unwrap(await NewController(db).Get(CancellationToken.None));

        var trig = stats.ArmedTriggers.Single();
        trig.NextFireKind.Should().Be("polling");
        trig.PollIntervalSeconds.Should().Be(30);
        trig.NextFireUtc.Should().BeNull();
    }

    [Fact]
    public async Task Get_WithMultipleScheduleNodes_ReturnsEarliestNextFire()
    {
        var db = TestDbFactory.Create();
        // Two cron nodes: one every minute (next ≤ 60s), one every hour at :00 (next up to 60min away).
        db.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "DualCron",
            IsEnabled = true,
            UpdatedAt = DateTime.UtcNow,
            DefinitionJson = """{"nodes":[{"id":"a","data":{"activityType":"scheduleTrigger","config":{"cronExpression":"0 0 * * * ?"}}},{"id":"b","data":{"activityType":"scheduleTrigger","config":{"cronExpression":"0 * * * * ?"}}}]}""",
            TriggerTypesJson = """["scheduleTrigger"]"""
        });
        await db.SaveChangesAsync();

        var stats = Unwrap(await NewController(db).Get(CancellationToken.None));

        var trig = stats.ArmedTriggers.Single();
        trig.NextFireKind.Should().Be("cron");
        trig.NextFireUtc.Should().NotBeNull();
        // The earliest of the two must be ≤ 60s from now (the per-minute schedule).
        trig.NextFireUtc!.Value.Should().BeBefore(DateTime.UtcNow.AddMinutes(2));
    }

    [Fact]
    public async Task Get_WithMalformedCron_FallsBackToNullSilent()
    {
        var db = TestDbFactory.Create();
        db.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Broken",
            IsEnabled = true,
            UpdatedAt = DateTime.UtcNow,
            DefinitionJson = """{"nodes":[{"id":"t","data":{"activityType":"scheduleTrigger","config":{"cronExpression":"not a cron"}}}]}""",
            TriggerTypesJson = """["scheduleTrigger"]"""
        });
        await db.SaveChangesAsync();

        var stats = Unwrap(await NewController(db).Get(CancellationToken.None));

        var trig = stats.ArmedTriggers.Single();
        trig.NextFireUtc.Should().BeNull();
        // No usable cron and no other trigger type → falls through to event-driven.
        trig.NextFireKind.Should().Be("event-driven");
    }

    [Fact]
    public async Task Get_TopWorkflows_IncludesAvgAndP95FromStats()
    {
        var db = TestDbFactory.Create();
        var wfId = Guid.NewGuid();
        db.Workflows.Add(new Workflow
        {
            Id = wfId, Name = "Measured", IsEnabled = true,
            UpdatedAt = DateTime.UtcNow, DefinitionJson = "{}"
        });
        for (var i = 0; i < 3; i++)
            db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = Guid.NewGuid(), WorkflowId = wfId,
                Status = ExecutionStatus.Succeeded, StartedAt = DateTime.UtcNow
            });
        db.WorkflowStats.Add(new WorkflowStats
        {
            WorkflowId = wfId, WindowDays = 7, TotalExecutions = 3,
            AvgDurationMsWindow = 12400, P95DurationMsWindow = 38000,
            RefreshedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var stats = Unwrap(await NewController(db).Get(CancellationToken.None));

        var top = stats.TopWorkflows.Single();
        top.AvgDurationMs.Should().Be(12400);
        top.P95DurationMs.Should().Be(38000);
    }

    [Fact]
    public async Task Get_FailingWorkflows_OrdersByFailCountDesc()
    {
        var db = TestDbFactory.Create();
        var hi = Guid.NewGuid();
        var lo = Guid.NewGuid();
        db.Workflows.AddRange(
            new Workflow { Id = hi, Name = "Frequently Failing", DefinitionJson = "{}", UpdatedAt = DateTime.UtcNow },
            new Workflow { Id = lo, Name = "Occasionally Failing", DefinitionJson = "{}", UpdatedAt = DateTime.UtcNow });
        for (var i = 0; i < 5; i++)
            db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = Guid.NewGuid(), WorkflowId = hi,
                Status = ExecutionStatus.Failed, StartedAt = DateTime.UtcNow.AddMinutes(-i)
            });
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = lo,
            Status = ExecutionStatus.Failed, StartedAt = DateTime.UtcNow.AddMinutes(-30)
        });
        await db.SaveChangesAsync();

        var stats = Unwrap(await NewController(db).Get(CancellationToken.None));

        stats.FailingWorkflows.Should().HaveCount(2);
        stats.FailingWorkflows[0].Name.Should().Be("Frequently Failing");
        stats.FailingWorkflows[0].FailCount.Should().Be(5);
        stats.FailingWorkflows[0].LastFailureAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Get_QueueDepth_CountsPendingRunningSeparately()
    {
        var db = TestDbFactory.Create();
        var wfId = Guid.NewGuid();
        db.Workflows.Add(new Workflow { Id = wfId, Name = "Q", DefinitionJson = "{}", UpdatedAt = DateTime.UtcNow });
        db.WorkflowExecutions.AddRange(
            new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = wfId, Status = ExecutionStatus.Pending, StartedAt = DateTime.UtcNow },
            new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = wfId, Status = ExecutionStatus.Pending, StartedAt = DateTime.UtcNow },
            new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = wfId, Status = ExecutionStatus.Running, StartedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var stats = Unwrap(await NewController(db).Get(CancellationToken.None));

        stats.PendingCount.Should().Be(2);
        stats.RunningCount.Should().Be(1);
    }

    [Fact]
    public async Task Get_LongRunning_OnlyCountsRunningOlderThan30min()
    {
        var db = TestDbFactory.Create();
        var wfId = Guid.NewGuid();
        db.Workflows.Add(new Workflow { Id = wfId, Name = "LR", DefinitionJson = "{}", UpdatedAt = DateTime.UtcNow });
        db.WorkflowExecutions.AddRange(
            // Recent Running — not long-running yet
            new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = wfId, Status = ExecutionStatus.Running, StartedAt = DateTime.UtcNow.AddMinutes(-5) },
            // 35min old Running → long-running
            new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = wfId, Status = ExecutionStatus.Running, StartedAt = DateTime.UtcNow.AddMinutes(-35) },
            // 35min old but Succeeded → not long-running (must be Running)
            new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = wfId, Status = ExecutionStatus.Succeeded, StartedAt = DateTime.UtcNow.AddMinutes(-35) });
        await db.SaveChangesAsync();

        var stats = Unwrap(await NewController(db).Get(CancellationToken.None));

        stats.LongRunningCount.Should().Be(1);
    }

    [Fact]
    public async Task Get_EditLocks_JoinsUsername_OrdersByOldestFirst()
    {
        var db = TestDbFactory.Create();
        var alice = new User { Id = Guid.NewGuid(), Username = "alice", Role = UserRole.Operator };
        var bob = new User { Id = Guid.NewGuid(), Username = "bob", Role = UserRole.Operator };
        db.Users.AddRange(alice, bob);
        db.Workflows.AddRange(
            new Workflow
            {
                Id = Guid.NewGuid(), Name = "Newer Lock", DefinitionJson = "{}", UpdatedAt = DateTime.UtcNow,
                CheckedOutByUserId = bob.Id, CheckedOutAt = DateTime.UtcNow.AddMinutes(-2)
            },
            new Workflow
            {
                Id = Guid.NewGuid(), Name = "Older Lock", DefinitionJson = "{}", UpdatedAt = DateTime.UtcNow,
                CheckedOutByUserId = alice.Id, CheckedOutAt = DateTime.UtcNow.AddHours(-2)
            },
            new Workflow { Id = Guid.NewGuid(), Name = "Unlocked", DefinitionJson = "{}", UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var stats = Unwrap(await NewController(db).Get(CancellationToken.None));

        stats.EditLocks.Should().HaveCount(2);
        stats.EditLocks[0].WorkflowName.Should().Be("Older Lock");
        stats.EditLocks[0].LockOwnerUserName.Should().Be("alice");
        stats.EditLocks[1].WorkflowName.Should().Be("Newer Lock");
        stats.EditLocks[1].LockOwnerUserName.Should().Be("bob");
    }

    [Fact]
    public async Task Get_AsAdmin_IncludesRecentAudit()
    {
        var db = TestDbFactory.Create();
        var user = new User { Id = Guid.NewGuid(), Username = "operator1", Role = UserRole.Operator };
        db.Users.Add(user);
        db.AuditLog.AddRange(
            new AuditLogEntry { Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow.AddMinutes(-5),
                Action = "WORKFLOW_PUBLISHED", ResourceType = "Workflow", UserId = user.Id, ResourceId = Guid.NewGuid() },
            // Excluded action — should not appear in the whitelisted feed.
            new AuditLogEntry { Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow.AddMinutes(-1),
                Action = "EXECUTION_STARTED", ResourceType = "Execution", UserId = user.Id });
        await db.SaveChangesAsync();

        var stats = Unwrap(await NewController(db, role: "Admin").Get(CancellationToken.None));

        stats.RecentAudit.Should().NotBeNull();
        stats.RecentAudit!.Should().HaveCount(1);
        stats.RecentAudit![0].Action.Should().Be("WORKFLOW_PUBLISHED");
        stats.RecentAudit![0].ActorUserName.Should().Be("operator1");
    }

    [Fact]
    public async Task Get_AsViewer_OmitsRecentAudit_Null()
    {
        var db = TestDbFactory.Create();
        db.AuditLog.Add(new AuditLogEntry
        {
            Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, Action = "WORKFLOW_PUBLISHED", ResourceType = "Workflow"
        });
        await db.SaveChangesAsync();

        var stats = Unwrap(await NewController(db, role: "Viewer").Get(CancellationToken.None));

        stats.RecentAudit.Should().BeNull();
    }

    [Fact]
    public async Task Get_RecentAudit_ActorUserNameNull_WhenUserMissing()
    {
        var db = TestDbFactory.Create();
        db.AuditLog.Add(new AuditLogEntry
        {
            Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow,
            Action = "LOGIN_LOCKED", ResourceType = "User", UserId = Guid.NewGuid()  // FK doesn't exist
        });
        await db.SaveChangesAsync();

        var stats = Unwrap(await NewController(db, role: "Admin").Get(CancellationToken.None));

        stats.RecentAudit.Should().NotBeNull();
        stats.RecentAudit!.Should().HaveCount(1);
        stats.RecentAudit![0].ActorUserName.Should().BeNull();
    }

    [Fact]
    public async Task Get_HealthHeartbeats_IsStale_WhenLastBeatOlderThan3xInterval()
    {
        var db = TestDbFactory.Create();
        db.SystemHealth.AddRange(
            new SystemHealthHeartbeat
            {
                ServiceName = "Fresh",
                LastHeartbeatAt = DateTime.UtcNow.AddSeconds(-10),
                ExpectedIntervalSeconds = 30
            },
            new SystemHealthHeartbeat
            {
                ServiceName = "Stale",
                // Last beat 200s ago vs expected 30s × 3 = 90s threshold → stale.
                LastHeartbeatAt = DateTime.UtcNow.AddSeconds(-200),
                ExpectedIntervalSeconds = 30
            });
        await db.SaveChangesAsync();

        var stats = Unwrap(await NewController(db).Get(CancellationToken.None));

        stats.HealthHeartbeats.Should().HaveCount(2);
        stats.HealthHeartbeats.Single(h => h.ServiceName == "Fresh").IsStale.Should().BeFalse();
        stats.HealthHeartbeats.Single(h => h.ServiceName == "Stale").IsStale.Should().BeTrue();
    }

    [Fact]
    public async Task Get_DatabaseProvider_NormalizedToLowercaseShortName()
    {
        // TestDbFactory builds a SQLite in-memory provider, which the controller maps to "sqlite".
        var db = TestDbFactory.Create();
        var stats = Unwrap(await NewController(db).Get(CancellationToken.None));
        stats.DatabaseProvider.Should().Be("sqlite");
    }

    [Fact]
    public async Task Get_ClusterRole_NullWhenProviderMissingOrNotConfigured()
    {
        var db = TestDbFactory.Create();
        var stats = Unwrap(await NewController(db, cluster: null).Get(CancellationToken.None));
        stats.ClusterRole.Should().BeNull();
    }

    [Fact]
    public async Task Get_ClusterRole_Leader_WhenLeaseHeld()
    {
        var db = TestDbFactory.Create();
        var stub = new StubClusterStateProvider(isLeader: true, leaseEpoch: 1, leaseExpires: DateTime.UtcNow.AddMinutes(1));
        var stats = Unwrap(await NewController(db, cluster: stub).Get(CancellationToken.None));
        stats.ClusterRole.Should().Be("leader");
    }

    [Fact]
    public async Task Get_ClusterRole_Standby_WhenFollower()
    {
        var db = TestDbFactory.Create();
        var stub = new StubClusterStateProvider(isLeader: false, leaseEpoch: 2, leaseExpires: null);
        var stats = Unwrap(await NewController(db, cluster: stub).Get(CancellationToken.None));
        stats.ClusterRole.Should().Be("standby");
    }

    private sealed class StubClusterStateProvider : IClusterStateProvider
    {
        public StubClusterStateProvider(bool isLeader, long leaseEpoch, DateTime? leaseExpires)
        {
            IsLeader = isLeader;
            LeaseEpoch = leaseEpoch;
            LeaseExpiresAt = leaseExpires;
        }
        public bool IsLeader { get; }
        public string NodeId => "test-node";
        public DateTime? LeaseExpiresAt { get; }
        public long LeaseEpoch { get; }
        public DateTime? LastSuccessfulRenewAt => null;
        public event Action<long>? OnLeadershipAcquired { add { } remove { } }
        public event Action? OnLeadershipLost { add { } remove { } }
    }
}
