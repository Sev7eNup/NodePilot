using FluentAssertions;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Scheduler.SystemAlerts;
using NodePilot.Scheduler.SystemAlerts.Sources;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Engine.Tests.SystemAlerts;

/// <summary>
/// Exercises each exemplar source's read-only <c>ObserveAsync</c> against seeded in-memory data, proving the
/// three archetypes (global metric, per-instance health, event) yield correctly-shaped observations.
/// </summary>
public class SystemAlertSourceTests
{
    private static Workflow Wf(Guid id, string name) => new() { Id = id, Name = name, DefinitionJson = "{}" };

    private static WorkflowExecution Exec(Guid workflowId, ExecutionStatus status, DateTime? completedAt = null, int callDepth = 0)
        => new()
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflowId,
            Status = status,
            StartedAt = (completedAt ?? DateTime.UtcNow).AddSeconds(-2),
            CompletedAt = status is ExecutionStatus.Pending or ExecutionStatus.Running ? null : completedAt ?? DateTime.UtcNow,
            CallDepth = callDepth,
        };

    // ---- BacklogSource (global metric) ----

    [Fact]
    public async Task BacklogSource_CountsPendingAndRunningOnly()
    {
        await using var db = TestDbFactory.Create();
        var wf = Wf(Guid.NewGuid(), "wf");
        db.Workflows.Add(wf);
        db.WorkflowExecutions.AddRange(
            Exec(wf.Id, ExecutionStatus.Pending),
            Exec(wf.Id, ExecutionStatus.Running),
            Exec(wf.Id, ExecutionStatus.Running),
            Exec(wf.Id, ExecutionStatus.Succeeded, DateTime.UtcNow));
        await db.SaveChangesAsync();

        var obs = await new BacklogSource().ObserveAsync(db, SystemAlertQuery.Empty, CancellationToken.None);

        var single = obs.Should().ContainSingle().Subject;
        single.InstanceKey.Should().Be("backlog");
        single.Fields["depth"].Should().Be(3);
    }

    // ---- MachineUnreachableSource (per-instance health) ----

    [Fact]
    public async Task MachineUnreachableSource_EmitsPerCheckedMachine_ExcludesUnchecked()
    {
        await using var db = TestDbFactory.Create();
        var reachable = new ManagedMachine { Id = Guid.NewGuid(), Name = "ok", Hostname = "ok", IsReachable = true, LastConnectivityCheck = DateTime.UtcNow.AddMinutes(-2) };
        var down = new ManagedMachine { Id = Guid.NewGuid(), Name = "down", Hostname = "down", IsReachable = false, LastConnectivityCheck = DateTime.UtcNow.AddMinutes(-5) };
        var never = new ManagedMachine { Id = Guid.NewGuid(), Name = "never", Hostname = "never", IsReachable = false, LastConnectivityCheck = null };
        db.ManagedMachines.AddRange(reachable, down, never);
        await db.SaveChangesAsync();

        var source = new MachineUnreachableSource();
        var obs = await source.ObserveAsync(db, SystemAlertQuery.Empty, CancellationToken.None);

        obs.Should().HaveCount(2, "the never-checked machine is unknown, not observed");
        obs.Select(o => o.TargetMachine).Should().BeEquivalentTo(["ok", "down"]);
        obs.Single(o => o.TargetMachine == "down").Fields["reachable"].Should().Be(false);
        obs.Single(o => o.TargetMachine == "ok").Fields["reachable"].Should().Be(true);
        obs.Single(o => o.TargetMachine == "down").InstanceKey.Should().Be(down.Id.ToString("N"));
    }

    [Fact]
    public async Task MachineUnreachableSource_IsAvailable_OnlyWhenAMachineHasBeenChecked()
    {
        await using var db = TestDbFactory.Create();
        var source = new MachineUnreachableSource();

        (await source.IsAvailableAsync(db, CancellationToken.None)).Should().BeFalse();

        db.ManagedMachines.Add(new ManagedMachine { Id = Guid.NewGuid(), Name = "m", Hostname = "m", LastConnectivityCheck = DateTime.UtcNow });
        await db.SaveChangesAsync();

        (await source.IsAvailableAsync(db, CancellationToken.None)).Should().BeTrue();
    }

    // ---- ExecutionResultSource (event) ----

    [Fact]
    public async Task ExecutionResultSource_ReturnsTerminalRunsWithinLookback_WithWorkflowScope()
    {
        await using var db = TestDbFactory.Create();
        var wf = Wf(Guid.NewGuid(), "Deploy");
        db.Workflows.Add(wf);
        var recentFail = Exec(wf.Id, ExecutionStatus.Failed, DateTime.UtcNow.AddSeconds(-30));
        recentFail.ErrorMessage = "boom";
        db.WorkflowExecutions.AddRange(
            recentFail,
            Exec(wf.Id, ExecutionStatus.Succeeded, DateTime.UtcNow.AddSeconds(-30), callDepth: 2),
            Exec(wf.Id, ExecutionStatus.Failed, DateTime.UtcNow.AddSeconds(-9999)), // outside lookback
            Exec(wf.Id, ExecutionStatus.Running));                                  // not terminal
        await db.SaveChangesAsync();

        var query = new SystemAlertQuery(new Dictionary<string, object?> { ["lookbackSeconds"] = 300 });
        var obs = await new ExecutionResultSource().ObserveAsync(db, query, CancellationToken.None);

        obs.Should().HaveCount(2);
        var fail = obs.Single(o => (string?)o.Fields["status"] == "Failed");
        fail.Fields["errorMessage"].Should().Be("boom");
        fail.WorkflowId.Should().Be(wf.Id);
        fail.WorkflowName.Should().Be("Deploy");
        fail.InstanceKey.Should().Be(recentFail.Id.ToString("N"));

        var ok = obs.Single(o => (string?)o.Fields["status"] == "Succeeded");
        ok.Fields["isSubWorkflow"].Should().Be(true);
        ((long)ok.Fields["durationMs"]!).Should().BeGreaterThan(0);
    }

    // ---- ported metric/health sources ----

    [Fact]
    public async Task PendingSource_CountsPendingOnly()
    {
        await using var db = TestDbFactory.Create();
        var wf = Wf(Guid.NewGuid(), "wf");
        db.Workflows.Add(wf);
        db.WorkflowExecutions.AddRange(
            Exec(wf.Id, ExecutionStatus.Pending),
            Exec(wf.Id, ExecutionStatus.Pending),
            Exec(wf.Id, ExecutionStatus.Running));
        await db.SaveChangesAsync();

        var obs = await new PendingSource().ObserveAsync(db, SystemAlertQuery.Empty, CancellationToken.None);

        obs.Should().ContainSingle().Which.Fields["pending"].Should().Be(2);
    }

    [Fact]
    public async Task CancelRateSource_CountsWithinWindow()
    {
        await using var db = TestDbFactory.Create();
        var wf = Wf(Guid.NewGuid(), "wf");
        db.Workflows.Add(wf);
        db.WorkflowExecutions.AddRange(
            Exec(wf.Id, ExecutionStatus.Cancelled, DateTime.UtcNow.AddMinutes(-2)),
            Exec(wf.Id, ExecutionStatus.Cancelled, DateTime.UtcNow.AddMinutes(-2)),
            Exec(wf.Id, ExecutionStatus.Cancelled, DateTime.UtcNow.AddMinutes(-90))); // outside default 10min
        await db.SaveChangesAsync();

        var obs = await new CancelRateSource().ObserveAsync(db, SystemAlertQuery.Empty, CancellationToken.None);

        obs.Should().ContainSingle().Which.Fields["cancels"].Should().Be(2);
    }

    [Fact]
    public async Task CredentialExpirySource_ReportsDaysLeft_AndAvailabilityGated()
    {
        await using var db = TestDbFactory.Create();
        var source = new CredentialExpirySource();
        (await source.IsAvailableAsync(db, CancellationToken.None)).Should().BeFalse("no credential tracks expiry");

        db.Credentials.Add(new Credential { Id = Guid.NewGuid(), Name = "svc", Username = "u", EncryptedPassword = [1], ExpiresAt = DateTime.UtcNow.AddDays(5) });
        db.Credentials.Add(new Credential { Id = Guid.NewGuid(), Name = "untracked", Username = "u", EncryptedPassword = [1], ExpiresAt = null });
        await db.SaveChangesAsync();

        (await source.IsAvailableAsync(db, CancellationToken.None)).Should().BeTrue();
        var obs = await source.ObserveAsync(db, SystemAlertQuery.Empty, CancellationToken.None);
        var single = obs.Should().ContainSingle().Subject;
        single.Fields["expired"].Should().Be(false);
        ((long)single.Fields["daysLeft"]!).Should().BeInRange(4, 5);
    }

    [Fact]
    public async Task ServiceStaleSource_ReportsHeartbeatAge()
    {
        await using var db = TestDbFactory.Create();
        db.SystemHealth.Add(new SystemHealthHeartbeat { ServiceName = "Dispatcher", LastHeartbeatAt = DateTime.UtcNow.AddSeconds(-300), ExpectedIntervalSeconds = 30 });
        await db.SaveChangesAsync();

        var obs = await new ServiceStaleSource().ObserveAsync(db, SystemAlertQuery.Empty, CancellationToken.None);

        var single = obs.Should().ContainSingle().Subject;
        single.InstanceKey.Should().Be("Dispatcher");
        ((long)single.Fields["staleSeconds"]!).Should().BeGreaterThan(250);
    }

    // ---- newer sources: stuck / workflow-health / alert-delivery-failed / schedule-missed ----

    [Fact]
    public async Task StuckExecutionSource_ReportsRunningMinutes_ForInFlightRuns()
    {
        await using var db = TestDbFactory.Create();
        var wf = Wf(Guid.NewGuid(), "Deploy");
        db.Workflows.Add(wf);
        db.WorkflowExecutions.Add(new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = wf.Id, Status = ExecutionStatus.Running, StartedAt = DateTime.UtcNow.AddMinutes(-40), CompletedAt = null });
        db.WorkflowExecutions.Add(Exec(wf.Id, ExecutionStatus.Succeeded, DateTime.UtcNow)); // terminal → ignored
        await db.SaveChangesAsync();

        var obs = await new StuckExecutionSource().ObserveAsync(db, SystemAlertQuery.Empty, CancellationToken.None);

        var single = obs.Should().ContainSingle().Subject;
        ((long)single.Fields["runningMinutes"]!).Should().BeInRange(39, 41);
        single.WorkflowName.Should().Be("Deploy");
    }

    [Fact]
    public async Task WorkflowHealthSource_ComputesFailureRate_FromStats()
    {
        await using var db = TestDbFactory.Create();
        var wf = Wf(Guid.NewGuid(), "Flaky");
        db.Workflows.Add(wf);
        db.WorkflowStats.Add(new WorkflowStats { WorkflowId = wf.Id, TotalExecutions = 10, SucceededWindow = 8, FailedWindow = 2, P95DurationMsWindow = 900, WindowDays = 30, RefreshedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var obs = await new WorkflowHealthSource().ObserveAsync(db, SystemAlertQuery.Empty, CancellationToken.None);

        var single = obs.Should().ContainSingle().Subject;
        single.Fields["failureRatePct"].Should().Be(20L);
        single.Fields["p95DurationMs"].Should().Be(900L);
        single.WorkflowId.Should().Be(wf.Id);
    }

    [Fact]
    public async Task AlertDeliveryFailureSource_CountsFailedDeliveriesInWindow()
    {
        await using var db = TestDbFactory.Create();
        for (var i = 0; i < 4; i++)
            db.NotificationDeliveryAttempts.Add(new NotificationDeliveryAttempt { Id = Guid.NewGuid(), NotificationRuleId = Guid.NewGuid(), NotificationRouteId = Guid.NewGuid(), EventKey = $"e{i}", DedupKey = "k", Status = NotificationDeliveryStatus.Failed, CreatedAt = DateTime.UtcNow.AddMinutes(-2) });
        db.NotificationDeliveryAttempts.Add(new NotificationDeliveryAttempt { Id = Guid.NewGuid(), NotificationRuleId = Guid.NewGuid(), NotificationRouteId = Guid.NewGuid(), EventKey = "sent", DedupKey = "k", Status = NotificationDeliveryStatus.Sent, CreatedAt = DateTime.UtcNow.AddMinutes(-2) });
        db.NotificationDeliveryAttempts.Add(new NotificationDeliveryAttempt { Id = Guid.NewGuid(), NotificationRuleId = Guid.NewGuid(), NotificationRouteId = Guid.NewGuid(), EventKey = "old", DedupKey = "k", Status = NotificationDeliveryStatus.Failed, CreatedAt = DateTime.UtcNow.AddHours(-2) }); // outside 15min
        await db.SaveChangesAsync();

        var obs = await new AlertDeliveryFailureSource().ObserveAsync(db, SystemAlertQuery.Empty, CancellationToken.None);

        obs.Should().ContainSingle().Which.Fields["failures"].Should().Be(4);
    }

    [Fact]
    public async Task ScheduleMissedSource_NoScheduledWorkflows_IsUnavailableAndEmpty()
    {
        await using var db = TestDbFactory.Create();
        var source = new ScheduleMissedSource();

        (await source.IsAvailableAsync(db, CancellationToken.None)).Should().BeFalse();
        (await source.ObserveAsync(db, SystemAlertQuery.Empty, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task ExecutionResultSource_LookbackParameter_BoundsTheWindow()
    {
        await using var db = TestDbFactory.Create();
        var wf = Wf(Guid.NewGuid(), "wf");
        db.Workflows.Add(wf);
        db.WorkflowExecutions.Add(Exec(wf.Id, ExecutionStatus.Failed, DateTime.UtcNow.AddSeconds(-120)));
        await db.SaveChangesAsync();

        var source = new ExecutionResultSource();
        var narrow = new SystemAlertQuery(new Dictionary<string, object?> { ["lookbackSeconds"] = 60 });
        var wide = new SystemAlertQuery(new Dictionary<string, object?> { ["lookbackSeconds"] = 600 });

        (await source.ObserveAsync(db, narrow, CancellationToken.None)).Should().BeEmpty();
        (await source.ObserveAsync(db, wide, CancellationToken.None)).Should().ContainSingle();
    }
}
