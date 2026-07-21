using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Security;
using NodePilot.Engine.Cluster;
using NodePilot.Scheduler;
using Xunit;

namespace NodePilot.Engine.Tests.Notifications;

/// <summary>
/// Covers the live "execution running too long" collector (CollectLongRunningAsync): fires once per
/// still-running execution via the runlong:{execId} idempotency key, respects scope, and recovers
/// crash-orphaned attempts (the ReconstructContextAsync runlong: branch).
/// </summary>
public class NotificationLongRunningTests
{
    private static byte[] Key()
    {
        var k = new byte[32];
        for (var i = 0; i < k.Length; i++) k[i] = (byte)(i + 3);
        return k;
    }

    private sealed class RecordingSink(NotificationChannel channel) : INotificationSink
    {
        public NotificationChannel Channel { get; } = channel;
        public List<NotificationContext> Sends { get; } = [];
        public Task<NotificationSendResult> SendAsync(NotificationContext ctx, string target, string? secret, CancellationToken ct)
        {
            Sends.Add(ctx);
            return Task.FromResult(NotificationSendResult.Ok);
        }
    }

    private static (NodePilotDbContext db, IServiceScopeFactory factory, SqliteConnection conn) CreateEnv()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(o => o.UseSqlite(conn));
        services.AddScoped<ISecretProtector>(_ => new AesGcmSecretProtector(Key()));
        services.AddScoped<INotificationRuleStore, NotificationRuleStore>();
        var sp = services.BuildServiceProvider();
        var outerDb = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(conn).Options);
        outerDb.Database.EnsureCreated();
        return (outerDb, sp.GetRequiredService<IServiceScopeFactory>(), conn);
    }

    private static NotificationDispatcher Build(IServiceScopeFactory factory, TimeSpan threshold, params INotificationSink[] sinks)
    {
        var d = new NotificationDispatcher(
            factory, new SingleNodeClusterStateProvider(), sinks,
            new NodePilot.Scheduler.SystemAlerts.SystemAlertCatalog(Array.Empty<NodePilot.Scheduler.SystemAlerts.ISystemAlertSource>()),
            new ConfigurationBuilder().Build(), NullLogger<NotificationDispatcher>.Instance);
        d.ScanSafetyLag = TimeSpan.Zero;
        d.LongRunningThreshold = threshold;
        d.QueuedLongThreshold = threshold;
        return d;
    }

    private static Guid SeedWorkflow(NodePilotDbContext db, string name = "WF")
    {
        var wf = new Workflow { Id = Guid.NewGuid(), Name = name, DefinitionJson = "{\"nodes\":[],\"edges\":[]}" };
        db.Workflows.Add(wf);
        db.SaveChanges();
        return wf.Id;
    }

    private static Guid SeedExecution(NodePilotDbContext db, Guid wfId, ExecutionStatus status, DateTime startedAt, DateTime? completedAt = null)
    {
        var id = Guid.NewGuid();
        db.WorkflowExecutions.Add(new WorkflowExecution
        { Id = id, WorkflowId = wfId, Status = status, StartedAt = startedAt, CompletedAt = completedAt });
        db.SaveChanges();
        return id;
    }

    private static Guid SeedRule(NodePilotDbContext db, string eventTypes, NotificationScopeKind scope = NotificationScopeKind.Global,
        Guid? workflowTarget = null, int cooldown = 0)
    {
        var rule = new NotificationRule
        {
            Id = Guid.NewGuid(),
            Name = $"rule-{Guid.NewGuid():N}",
            EventTypes = eventTypes,
            ScopeKind = scope,
            IsEnabled = true,
            CooldownMinutes = cooldown,
            Routes = [new NotificationRoute { Id = Guid.NewGuid(), Channel = NotificationChannel.Email, Target = "a@x", Order = 0 }],
            Targets = workflowTarget is { } wf
                ? [new NotificationRuleTarget { Id = Guid.NewGuid(), TargetKind = NotificationTargetKind.Workflow, TargetId = wf }]
                : [],
        };
        db.NotificationRules.Add(rule);
        db.SaveChanges();
        return rule.Id;
    }

    [Fact]
    public async Task LongRunner_FiresOnce_PerExecution()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            SeedRule(db, "ExecutionRunningLong");
            var execId = SeedExecution(db, wf, ExecutionStatus.Running, DateTime.UtcNow.AddMinutes(-5));
            var email = new RecordingSink(NotificationChannel.Email);
            var dispatcher = Build(factory, TimeSpan.FromSeconds(60), email);

            await dispatcher.DispatchOnceAsync(CancellationToken.None); // crosses the threshold → fire
            await dispatcher.DispatchOnceAsync(CancellationToken.None); // still running → existence-check dedups

            email.Sends.Should().ContainSingle("one alert per running execution, deduped across passes");
            email.Sends[0].EventType.Should().Be(NotificationEventType.ExecutionRunningLong);
            email.Sends[0].EventKey.Should().Be($"runlong:{execId:N}");
            email.Sends[0].DurationMs.Should().BeGreaterThan(0);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task ShortRunner_DoesNotFire()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            SeedRule(db, "ExecutionRunningLong");
            SeedExecution(db, wf, ExecutionStatus.Running, DateTime.UtcNow.AddSeconds(-5)); // younger than the threshold
            var email = new RecordingSink(NotificationChannel.Email);

            await Build(factory, TimeSpan.FromSeconds(60), email).DispatchOnceAsync(CancellationToken.None);

            email.Sends.Should().BeEmpty("execution hasn't been running long enough yet");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task CompletedExecution_NotScanned()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            SeedRule(db, "ExecutionRunningLong");
            // A long execution that already finished must not fire the live long-running signal.
            SeedExecution(db, wf, ExecutionStatus.Succeeded, DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow.AddMinutes(-1));
            var email = new RecordingSink(NotificationChannel.Email);

            await Build(factory, TimeSpan.FromSeconds(60), email).DispatchOnceAsync(CancellationToken.None);

            email.Sends.Should().BeEmpty("only RUNNING executions are scanned for long-running");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task NoLongRunningRule_SkipsScan()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            SeedRule(db, "ExecutionFailed"); // nothing wants ExecutionRunningLong
            SeedExecution(db, wf, ExecutionStatus.Running, DateTime.UtcNow.AddMinutes(-5));
            var email = new RecordingSink(NotificationChannel.Email);

            await Build(factory, TimeSpan.FromSeconds(60), email).DispatchOnceAsync(CancellationToken.None);

            email.Sends.Should().BeEmpty("the running scan is skipped when no rule references ExecutionRunningLong");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task RespectsWorkflowScope()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wfA = SeedWorkflow(db, "A");
            var wfB = SeedWorkflow(db, "B");
            SeedRule(db, "ExecutionRunningLong", NotificationScopeKind.Workflows, workflowTarget: wfA);
            SeedExecution(db, wfA, ExecutionStatus.Running, DateTime.UtcNow.AddMinutes(-5));
            SeedExecution(db, wfB, ExecutionStatus.Running, DateTime.UtcNow.AddMinutes(-5));
            var email = new RecordingSink(NotificationChannel.Email);

            await Build(factory, TimeSpan.FromSeconds(60), email).DispatchOnceAsync(CancellationToken.None);

            email.Sends.Should().ContainSingle("only the in-scope workflow's long-runner alerts");
            email.Sends[0].WorkflowName.Should().Be("A");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task CrashRecovery_ReconstructsRunlongContext()
    {
        // A runlong: attempt left Pending by a crash between persist and send must be re-derived from the
        // still-running row and delivered — NOT failed out (which the exec:/gauge:-only parser would do).
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            var ruleId = SeedRule(db, "ExecutionRunningLong");
            var routeId = await db.NotificationRoutes.AsNoTracking().Where(r => r.NotificationRuleId == ruleId).Select(r => r.Id).FirstAsync();
            var execId = SeedExecution(db, wf, ExecutionStatus.Running, DateTime.UtcNow.AddMinutes(-5));
            db.NotificationDeliveryAttempts.Add(new NotificationDeliveryAttempt
            {
                Id = Guid.NewGuid(),
                NotificationRuleId = ruleId,
                NotificationRouteId = routeId,
                EventKey = $"runlong:{execId:N}",
                DedupKey = $"{ruleId}:{wf}:ExecutionRunningLong",
                Status = NotificationDeliveryStatus.Pending,
                CreatedAt = DateTime.UtcNow.AddMinutes(-1),
            });
            await db.SaveChangesAsync();
            var email = new RecordingSink(NotificationChannel.Email);

            await Build(factory, TimeSpan.FromSeconds(60), email).DispatchOnceAsync(CancellationToken.None);

            email.Sends.Should().ContainSingle("the orphaned runlong: attempt is reconstructed and delivered");
            (await db.NotificationDeliveryAttempts.AsNoTracking().SingleAsync(a => a.EventKey == $"runlong:{execId:N}"))
                .Status.Should().Be(NotificationDeliveryStatus.Sent);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task QueuedLong_FiresOnce_PerExecution()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            SeedRule(db, "ExecutionQueuedLong");
            var execId = SeedExecution(db, wf, ExecutionStatus.Pending, DateTime.UtcNow.AddMinutes(-5));
            var email = new RecordingSink(NotificationChannel.Email);
            var dispatcher = Build(factory, TimeSpan.FromSeconds(60), email);

            await dispatcher.DispatchOnceAsync(CancellationToken.None);
            await dispatcher.DispatchOnceAsync(CancellationToken.None);

            email.Sends.Should().ContainSingle("one alert per pending execution, deduped across passes");
            email.Sends[0].EventType.Should().Be(NotificationEventType.ExecutionQueuedLong);
            email.Sends[0].EventKey.Should().Be($"queuedlong:{execId:N}");
            email.Sends[0].Status.Should().Be("Pending");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task QueuedLong_RespectsWorkflowScope()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wfA = SeedWorkflow(db, "A");
            var wfB = SeedWorkflow(db, "B");
            SeedRule(db, "ExecutionQueuedLong", NotificationScopeKind.Workflows, workflowTarget: wfA);
            SeedExecution(db, wfA, ExecutionStatus.Pending, DateTime.UtcNow.AddMinutes(-5));
            SeedExecution(db, wfB, ExecutionStatus.Pending, DateTime.UtcNow.AddMinutes(-5));
            var email = new RecordingSink(NotificationChannel.Email);

            await Build(factory, TimeSpan.FromSeconds(60), email).DispatchOnceAsync(CancellationToken.None);

            email.Sends.Should().ContainSingle("only the in-scope workflow's queued execution alerts");
            email.Sends[0].WorkflowName.Should().Be("A");
        }
        finally { conn.Dispose(); }
    }
}
