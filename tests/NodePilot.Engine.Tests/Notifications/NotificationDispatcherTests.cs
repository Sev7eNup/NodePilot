using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
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

public class NotificationDispatcherTests
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
        public List<(NotificationContext ctx, string target, string? secret)> Sends { get; } = [];
        public Func<NotificationSendResult>? Behavior { get; set; }

        public Task<NotificationSendResult> SendAsync(NotificationContext ctx, string target, string? secret, CancellationToken ct)
        {
            Sends.Add((ctx, target, secret));
            return Task.FromResult(Behavior?.Invoke() ?? NotificationSendResult.Ok);
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

    private static NotificationDispatcher Build(IServiceScopeFactory factory, params INotificationSink[] sinks)
    {
        var d = new NotificationDispatcher(
            factory, new SingleNodeClusterStateProvider(), sinks,
            new NodePilot.Scheduler.SystemAlerts.SystemAlertCatalog(Array.Empty<NodePilot.Scheduler.SystemAlerts.ISystemAlertSource>()),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<NotificationDispatcher>.Instance);
        d.ScanSafetyLag = TimeSpan.Zero; // tests seed executions at "now"; disable the commit-visibility lag
        return d;
    }

    private static NotificationDispatcher BuildWithConfig(
        IServiceScopeFactory factory, Microsoft.Extensions.Configuration.IConfiguration config, params INotificationSink[] sinks)
    {
        var d = new NotificationDispatcher(
            factory, new SingleNodeClusterStateProvider(), sinks,
            new NodePilot.Scheduler.SystemAlerts.SystemAlertCatalog(Array.Empty<NodePilot.Scheduler.SystemAlerts.ISystemAlertSource>()),
            config, NullLogger<NotificationDispatcher>.Instance);
        d.ScanSafetyLag = TimeSpan.Zero;
        return d;
    }

    private static Guid SeedWorkflow(NodePilotDbContext db, string name = "WF")
    {
        var wf = new Workflow { Id = Guid.NewGuid(), Name = name, DefinitionJson = "{\"nodes\":[],\"edges\":[]}" };
        db.Workflows.Add(wf);
        db.SaveChanges();
        return wf.Id;
    }

    private static Guid SeedExecution(NodePilotDbContext db, Guid wfId, ExecutionStatus status, DateTime completedAt, string? errorMessage = null)
    {
        var id = Guid.NewGuid();
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = id,
            WorkflowId = wfId,
            Status = status,
            StartedAt = completedAt.AddSeconds(-1),
            CompletedAt = completedAt,
            ErrorMessage = errorMessage,
        });
        db.SaveChanges();
        return id;
    }

    private static void SeedWatermark(NodePilotDbContext db, DateTime lastSeen)
    {
        db.NotificationDispatcherStates.Add(new NotificationDispatcherState
        {
            Id = NotificationDispatcherState.SingletonId,
            LastCompletedAtSeen = lastSeen,
            LastIdSeen = Guid.Empty,
            UpdatedAt = lastSeen,
        });
        db.SaveChanges();
    }

    private static Guid SeedRule(NodePilotDbContext db, string eventTypes, params NotificationRoute[] routes)
    {
        var rule = new NotificationRule
        {
            Id = Guid.NewGuid(),
            Name = $"rule-{Guid.NewGuid():N}",
            EventTypes = eventTypes,
            ScopeKind = NotificationScopeKind.Global,
            IsEnabled = true,
            Routes = routes.ToList(),
        };
        db.NotificationRules.Add(rule);
        db.SaveChanges();
        return rule.Id;
    }

    private static NotificationRoute Route(NotificationChannel ch, string target, int order = 0)
        => new() { Id = Guid.NewGuid(), Channel = ch, Target = target, Order = order };

    [Fact]
    public async Task FirstRun_SeedsWatermark_DoesNotBackAlertHistory()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            SeedExecution(db, wf, ExecutionStatus.Failed, DateTime.UtcNow.AddMinutes(-5)); // pre-existing history
            SeedRule(db, "ExecutionFailed", Route(NotificationChannel.Email, "a@x"));
            var email = new RecordingSink(NotificationChannel.Email);

            var sent = await Build(factory, email).DispatchOnceAsync(CancellationToken.None);

            sent.Should().Be(0);
            email.Sends.Should().BeEmpty("first run seeds the watermark to now — existing history is never back-alerted");
            (await db.NotificationDispatcherStates.CountAsync()).Should().Be(1);
            (await db.NotificationDeliveryAttempts.CountAsync()).Should().Be(0);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task MatchingFailure_FansOutToAllRoutes()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            SeedWatermark(db, DateTime.UtcNow.AddHours(-1));
            SeedRule(db, "ExecutionFailed",
                Route(NotificationChannel.Email, "a@x", 0),
                Route(NotificationChannel.GenericWebhook, "https://hook", 1));
            SeedExecution(db, wf, ExecutionStatus.Failed, DateTime.UtcNow);
            var email = new RecordingSink(NotificationChannel.Email);
            var hook = new RecordingSink(NotificationChannel.GenericWebhook);

            var sent = await Build(factory, email, hook).DispatchOnceAsync(CancellationToken.None);

            sent.Should().Be(2);
            email.Sends.Should().HaveCount(1);
            hook.Sends.Should().HaveCount(1);
            (await db.NotificationDeliveryAttempts.CountAsync(a => a.Status == NotificationDeliveryStatus.Sent)).Should().Be(2);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task RouteCondition_FiltersIndividualRoutes()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            SeedWatermark(db, DateTime.UtcNow.AddHours(-1));
            const string succeededOnly = """
            {"type":"comparison","op":"==","left":{"kind":"variable","source":"event","name":"status"},"right":{"kind":"literal","value":"Succeeded"}}
            """;
            SeedRule(db, "ExecutionFailed",
                Route(NotificationChannel.Email, "a@x", 0),
                new NotificationRoute { Id = Guid.NewGuid(), Channel = NotificationChannel.GenericWebhook, Target = "https://hook", Order = 1, ConditionExpressionJson = succeededOnly });
            SeedExecution(db, wf, ExecutionStatus.Failed, DateTime.UtcNow);
            var email = new RecordingSink(NotificationChannel.Email);
            var hook = new RecordingSink(NotificationChannel.GenericWebhook);

            var sent = await Build(factory, email, hook).DispatchOnceAsync(CancellationToken.None);

            sent.Should().Be(1);
            email.Sends.Should().HaveCount(1);
            hook.Sends.Should().BeEmpty("the webhook route condition does not match the failed event status");
            (await db.NotificationDeliveryAttempts.CountAsync()).Should().Be(1);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task DedupKeyTemplate_ChangesCooldownGrouping()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf1 = SeedWorkflow(db, "WF1");
            var wf2 = SeedWorkflow(db, "WF2");
            SeedWatermark(db, DateTime.UtcNow.AddHours(-1));
            var ruleId = SeedRule(db, "ExecutionFailed", Route(NotificationChannel.Email, "a@x"));
            var rule = await db.NotificationRules.FirstAsync(r => r.Id == ruleId);
            rule.CooldownMinutes = 60;
            rule.DedupKeyTemplate = "{{eventType}}";
            await db.SaveChangesAsync();
            SeedExecution(db, wf1, ExecutionStatus.Failed, DateTime.UtcNow.AddSeconds(-2));
            SeedExecution(db, wf2, ExecutionStatus.Failed, DateTime.UtcNow);
            var email = new RecordingSink(NotificationChannel.Email);

            await Build(factory, email).DispatchOnceAsync(CancellationToken.None);

            email.Sends.Should().HaveCount(1, "both failures render the same dedup key and share one cooldown bucket");
            (await db.NotificationSuppressionStates.Select(s => s.DedupKey).SingleAsync()).Should().Be("ExecutionFailed");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task Rescan_AfterWatermarkRollback_DoesNotDoubleSend()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            SeedWatermark(db, DateTime.UtcNow.AddHours(-1));
            SeedRule(db, "ExecutionFailed", Route(NotificationChannel.Email, "a@x"));
            SeedExecution(db, wf, ExecutionStatus.Failed, DateTime.UtcNow);
            var email = new RecordingSink(NotificationChannel.Email);
            var dispatcher = Build(factory, email);

            await dispatcher.DispatchOnceAsync(CancellationToken.None);
            // Simulate a crash that lost the advanced watermark: roll it back so the same execution is rescanned.
            var state = await db.NotificationDispatcherStates.FirstAsync();
            state.LastCompletedAtSeen = DateTime.UtcNow.AddHours(-1);
            state.LastIdSeen = Guid.Empty;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await dispatcher.DispatchOnceAsync(CancellationToken.None);

            email.Sends.Should().HaveCount(1, "the (rule,route,eventKey) delivery attempt already exists → no duplicate send");
            (await db.NotificationDeliveryAttempts.CountAsync()).Should().Be(1);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task Cooldown_SuppressesSecondOccurrenceOfSameWorkflow()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            SeedWatermark(db, DateTime.UtcNow.AddHours(-1));
            var ruleId = SeedRule(db, "ExecutionFailed", Route(NotificationChannel.Email, "a@x"));
            var rule = await db.NotificationRules.FirstAsync(r => r.Id == ruleId);
            rule.CooldownMinutes = 60;
            await db.SaveChangesAsync();
            SeedExecution(db, wf, ExecutionStatus.Failed, DateTime.UtcNow.AddSeconds(-2));
            SeedExecution(db, wf, ExecutionStatus.Failed, DateTime.UtcNow);
            var email = new RecordingSink(NotificationChannel.Email);

            await Build(factory, email).DispatchOnceAsync(CancellationToken.None);

            email.Sends.Should().HaveCount(1, "second failure of the same workflow is within the cooldown for its dedup key");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task EventTypePrefilter_IgnoresNonMatchingStatus()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            SeedWatermark(db, DateTime.UtcNow.AddHours(-1));
            SeedRule(db, "ExecutionFailed", Route(NotificationChannel.Email, "a@x"));
            SeedExecution(db, wf, ExecutionStatus.Succeeded, DateTime.UtcNow); // succeeded, rule only wants failures
            var email = new RecordingSink(NotificationChannel.Email);

            var sent = await Build(factory, email).DispatchOnceAsync(CancellationToken.None);

            sent.Should().Be(0);
            email.Sends.Should().BeEmpty();
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task CredentialFailure_DerivedFromFailedExecutionError()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            SeedWatermark(db, DateTime.UtcNow.AddHours(-1));
            SeedRule(db, "CredentialFailure", Route(NotificationChannel.Email, "a@x"));
            var execId = SeedExecution(db, wf, ExecutionStatus.Failed, DateTime.UtcNow, "Access denied: invalid password");
            var email = new RecordingSink(NotificationChannel.Email);

            var sent = await Build(factory, email).DispatchOnceAsync(CancellationToken.None);

            sent.Should().Be(1);
            email.Sends.Should().ContainSingle();
            email.Sends[0].ctx.EventType.Should().Be(NotificationEventType.CredentialFailure);
            email.Sends[0].ctx.EventKey.Should().Be($"exec:{execId:N}:CredentialFailure");
            email.Sends[0].ctx.ErrorMessage.Should().Contain("invalid password");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task TransientFailure_RetriedOnNextPass_ThenSucceeds()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            SeedWatermark(db, DateTime.UtcNow.AddHours(-1));
            SeedRule(db, "ExecutionFailed", Route(NotificationChannel.Email, "a@x"));
            SeedExecution(db, wf, ExecutionStatus.Failed, DateTime.UtcNow);
            var calls = 0;
            var email = new RecordingSink(NotificationChannel.Email)
            {
                Behavior = () => ++calls == 1 ? NotificationSendResult.Fail("smtp down") : NotificationSendResult.Ok,
            };
            var dispatcher = Build(factory, email);

            await dispatcher.DispatchOnceAsync(CancellationToken.None); // first send fails -> stays Pending
            (await db.NotificationDeliveryAttempts.AsNoTracking().SingleAsync()).Status
                .Should().Be(NotificationDeliveryStatus.Pending, "a transient failure must remain retryable, not be marked Failed");

            await dispatcher.DispatchOnceAsync(CancellationToken.None); // recovery retries -> succeeds

            email.Sends.Should().HaveCount(2);
            (await db.NotificationDeliveryAttempts.AsNoTracking().SingleAsync()).Status.Should().Be(NotificationDeliveryStatus.Sent);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task PersistentFailure_GivesUpAsFailed_AfterMaxAttempts()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            SeedWatermark(db, DateTime.UtcNow.AddHours(-1));
            SeedRule(db, "ExecutionFailed", Route(NotificationChannel.Email, "a@x"));
            SeedExecution(db, wf, ExecutionStatus.Failed, DateTime.UtcNow);
            var email = new RecordingSink(NotificationChannel.Email) { Behavior = () => NotificationSendResult.Fail("down") };
            var dispatcher = Build(factory, email);

            for (var i = 0; i < 8; i++) await dispatcher.DispatchOnceAsync(CancellationToken.None);

            email.Sends.Should().HaveCount(5, "bounded retry caps at MaxAttempts=5 then stops re-queuing");
            (await db.NotificationDeliveryAttempts.AsNoTracking().SingleAsync()).Status.Should().Be(NotificationDeliveryStatus.Failed);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task CorruptRouteSecret_DoesNotThrowOrWedgeThePass()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            SeedWatermark(db, DateTime.UtcNow.AddHours(-1));
            // A route whose stored cipher is not valid base64 -> GetRouteSecretAsync throws on decrypt.
            var route = Route(NotificationChannel.GenericWebhook, "https://hook");
            route.Secret = "!!!not-base64!!!";
            SeedRule(db, "ExecutionFailed", route);
            SeedExecution(db, wf, ExecutionStatus.Failed, DateTime.UtcNow);
            var hook = new RecordingSink(NotificationChannel.GenericWebhook);

            var act = async () => await Build(factory, hook).DispatchOnceAsync(CancellationToken.None);

            await act.Should().NotThrowAsync("a corrupt secret must be contained per-attempt, never abort the whole pass");
            hook.Sends.Should().BeEmpty("the throw happens during decrypt, before the sink is invoked");
            var attempt = await db.NotificationDeliveryAttempts.AsNoTracking().SingleAsync();
            attempt.Status.Should().Be(NotificationDeliveryStatus.Pending, "treated as a bounded-retry failure, not a poison pill");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task CancelledByFilter_TargetsManualCancelsOnly()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            SeedWatermark(db, DateTime.UtcNow.AddHours(-1));
            const string filter = """
            {"type":"comparison","op":"==","left":{"kind":"variable","source":"event","name":"cancelledBy"},"right":{"kind":"literal","value":"user"}}
            """;
            db.NotificationRules.Add(new NotificationRule
            {
                Id = Guid.NewGuid(),
                Name = "manual-cancel",
                EventTypes = "ExecutionCancelled",
                ScopeKind = NotificationScopeKind.Global,
                IsEnabled = true,
                FilterExpressionJson = filter,
                Routes = [Route(NotificationChannel.Email, "a@x")],
            });
            db.WorkflowExecutions.Add(new WorkflowExecution
            { Id = Guid.NewGuid(), WorkflowId = wf, Status = ExecutionStatus.Cancelled, StartedAt = DateTime.UtcNow.AddSeconds(-3), CompletedAt = DateTime.UtcNow.AddSeconds(-2), CancelledBy = "user" });
            db.WorkflowExecutions.Add(new WorkflowExecution
            { Id = Guid.NewGuid(), WorkflowId = wf, Status = ExecutionStatus.Cancelled, StartedAt = DateTime.UtcNow.AddSeconds(-3), CompletedAt = DateTime.UtcNow, CancelledBy = "cancelAll" });
            db.SaveChanges();
            var email = new RecordingSink(NotificationChannel.Email);

            await Build(factory, email).DispatchOnceAsync(CancellationToken.None);

            email.Sends.Should().ContainSingle("only the user-initiated cancel matches cancelledBy == \"user\"");
            email.Sends[0].ctx.CancelledBy.Should().Be("user");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task SafetyLag_SkipsExecutionsNewerThanTheLag()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            SeedWatermark(db, DateTime.UtcNow.AddHours(-1));
            SeedRule(db, "ExecutionFailed", Route(NotificationChannel.Email, "a@x"));
            SeedExecution(db, wf, ExecutionStatus.Failed, DateTime.UtcNow); // within the lag window
            var email = new RecordingSink(NotificationChannel.Email);
            var dispatcher = Build(factory, email);
            dispatcher.ScanSafetyLag = TimeSpan.FromMinutes(1);

            (await dispatcher.DispatchOnceAsync(CancellationToken.None)).Should().Be(0, "too-recent rows are held back to dodge the commit-visibility race");
            email.Sends.Should().BeEmpty();

            SeedExecution(db, wf, ExecutionStatus.Failed, DateTime.UtcNow.AddMinutes(-5)); // safely older than the lag
            await dispatcher.DispatchOnceAsync(CancellationToken.None);
            email.Sends.Should().HaveCount(1);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task FailedExecution_ContextCarriesLastFailingStepTargetMachine()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            SeedWatermark(db, DateTime.UtcNow.AddHours(-1));
            SeedRule(db, "ExecutionFailed", Route(NotificationChannel.Email, "a@x"));
            var execId = SeedExecution(db, wf, ExecutionStatus.Failed, DateTime.UtcNow.AddMinutes(-1), errorMessage: "boom");
            // Two failed steps — the LAST-completing one must win; a machine-less failed
            // step (engine-local) and a succeeded remote step must never be picked.
            db.StepExecutions.AddRange(
                new StepExecution
                {
                    Id = Guid.NewGuid(), WorkflowExecutionId = execId, StepId = "s1", StepType = "runScript",
                    Status = ExecutionStatus.Failed, TargetMachine = "SRV-OLD",
                    StartedAt = DateTime.UtcNow.AddMinutes(-3), CompletedAt = DateTime.UtcNow.AddMinutes(-2),
                },
                new StepExecution
                {
                    Id = Guid.NewGuid(), WorkflowExecutionId = execId, StepId = "s2", StepType = "log",
                    Status = ExecutionStatus.Failed, TargetMachine = null,
                    StartedAt = DateTime.UtcNow.AddMinutes(-2), CompletedAt = DateTime.UtcNow.AddSeconds(-30),
                },
                new StepExecution
                {
                    Id = Guid.NewGuid(), WorkflowExecutionId = execId, StepId = "s3", StepType = "runScript",
                    Status = ExecutionStatus.Failed, TargetMachine = "SRV-01",
                    StartedAt = DateTime.UtcNow.AddMinutes(-2), CompletedAt = DateTime.UtcNow.AddSeconds(-65),
                },
                new StepExecution
                {
                    Id = Guid.NewGuid(), WorkflowExecutionId = execId, StepId = "s4", StepType = "runScript",
                    Status = ExecutionStatus.Succeeded, TargetMachine = "SRV-OK",
                    StartedAt = DateTime.UtcNow.AddMinutes(-3), CompletedAt = DateTime.UtcNow.AddSeconds(-10),
                });
            db.SaveChanges();
            var email = new RecordingSink(NotificationChannel.Email);

            await Build(factory, email).DispatchOnceAsync(CancellationToken.None);

            email.Sends.Should().ContainSingle();
            email.Sends[0].ctx.TargetMachine.Should().Be("SRV-01",
                "the last-completing failed step with a machine wins (s2 has none, s1 failed earlier, s4 succeeded)");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task SucceededExecution_ContextTargetMachineStaysNull()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            SeedWatermark(db, DateTime.UtcNow.AddHours(-1));
            SeedRule(db, "ExecutionSucceeded", Route(NotificationChannel.Email, "a@x"));
            var execId = SeedExecution(db, wf, ExecutionStatus.Succeeded, DateTime.UtcNow.AddMinutes(-1));
            db.StepExecutions.Add(new StepExecution
            {
                Id = Guid.NewGuid(), WorkflowExecutionId = execId, StepId = "s1", StepType = "runScript",
                Status = ExecutionStatus.Succeeded, TargetMachine = "SRV-OK",
                StartedAt = DateTime.UtcNow.AddMinutes(-2), CompletedAt = DateTime.UtcNow.AddMinutes(-1),
            });
            db.SaveChanges();
            var email = new RecordingSink(NotificationChannel.Email);

            await Build(factory, email).DispatchOnceAsync(CancellationToken.None);

            email.Sends.Should().ContainSingle();
            email.Sends[0].ctx.TargetMachine.Should().BeNull("only FAILED steps feed the join");
        }
        finally { conn.Dispose(); }
    }

    /// <summary>
    /// Hot-reload: DispatchOnceAsync overlays Alerting:LongRunningSeconds from the live
    /// IConfiguration every pass, so changing it in the Settings UI re-tunes the
    /// ExecutionRunningLong threshold without a restart. Seed a Running execution 60s old and a
    /// rule for ExecutionRunningLong. With the threshold at 120s the execution is too young to
    /// alert; mutate the SAME config instance to 10s and the next pass on the SAME dispatcher
    /// fires — proving the per-pass overlay re-read the live value.
    /// </summary>
    [Fact]
    public async Task DispatchOnceAsync_LongRunningSeconds_OverlayFlipsLiveAfterConfigReload()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            var runningId = Guid.NewGuid();
            db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = runningId,
                WorkflowId = wf,
                Status = ExecutionStatus.Running,
                StartedAt = DateTime.UtcNow.AddSeconds(-60),
                CompletedAt = null,
            });
            await db.SaveChangesAsync();
            SeedRule(db, "ExecutionRunningLong", Route(NotificationChannel.Email, "a@x"));
            var email = new RecordingSink(NotificationChannel.Email);

            // Reloadable in-memory config: hold the provider so we can Set a key between passes.
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Alerting:LongRunningSeconds"] = "120",
                })
                .Build();
            var memProvider = config.Providers
                .OfType<Microsoft.Extensions.Configuration.Memory.MemoryConfigurationProvider>()
                .Single();
            var dispatcher = BuildWithConfig(factory, config, email);

            // Pass 1: threshold 120s → a 60s-old run is too young → no alert.
            (await dispatcher.DispatchOnceAsync(CancellationToken.None)).Should().Be(0);
            email.Sends.Should().BeEmpty();

            // Operator lowers Alerting:LongRunningSeconds in the Settings UI → config reload.
            memProvider.Set("Alerting:LongRunningSeconds", "10");

            // Same dispatcher instance: the next pass overlays the new 10s threshold → 60s-old
            // run now qualifies → exactly one alert (per-execution existence-check dedup).
            (await dispatcher.DispatchOnceAsync(CancellationToken.None)).Should().Be(1);
            email.Sends.Should().ContainSingle();
        }
        finally { conn.Dispose(); }
    }
}
