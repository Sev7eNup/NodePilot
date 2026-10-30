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
using NodePilot.Scheduler.SystemAlerts;
using NodePilot.Scheduler.SystemAlerts.Sources;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Engine.Tests.Notifications;

public class NotificationDispatcherTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ElapsedAlertResampling_DoesNotCountOrCooldownTwice(bool minimumOccurrences)
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var workflow = SeedWorkflow(db);
            var ruleId = SeedRule(db, "ExecutionRunningLong", Route(NotificationChannel.Email, "a@x"));
            var rule = db.NotificationRules.Single(r => r.Id == ruleId);
            rule.DedupKeyTemplate = "{{workflowId}}";
            rule.CooldownMinutes = minimumOccurrences ? 0 : 1;
            rule.MinOccurrences = minimumOccurrences ? 3 : 1;
            rule.OccurrenceWindowMinutes = 10;
            Guid AddRunning()
            {
                var id = Guid.NewGuid();
                db.WorkflowExecutions.Add(new WorkflowExecution { Id = id, WorkflowId = workflow,
                    Status = ExecutionStatus.Running, StartedAt = DateTime.UtcNow.AddHours(-2) });
                db.SaveChanges();
                return id;
            }
            AddRunning();
            var sink = new RecordingSink(NotificationChannel.Email);
            for (var pass = 0; pass < 3; pass++)
                await Build(factory, new SingleNodeClusterStateProvider(), sink).DispatchOnceAsync(TestContext.Current.CancellationToken);
            sink.Sends.Should().HaveCount(minimumOccurrences ? 0 : 1);
            AddRunning();
            if (!minimumOccurrences)
            {
                await Build(factory, new SingleNodeClusterStateProvider(), sink).DispatchOnceAsync(TestContext.Current.CancellationToken);
                sink.Sends.Should().ContainSingle("a new event waits while the cooldown is active");
                await db.NotificationSuppressionStates.ExecuteUpdateAsync(s => s.SetProperty(x => x.LastFiredAt, DateTime.UtcNow.AddMinutes(-2)));
            }
            await Build(factory, new SingleNodeClusterStateProvider(), sink).DispatchOnceAsync(TestContext.Current.CancellationToken);
            if (minimumOccurrences)
            {
                sink.Sends.Should().BeEmpty();
                AddRunning();
                await Build(factory, new SingleNodeClusterStateProvider(), sink).DispatchOnceAsync(TestContext.Current.CancellationToken);
            }
            sink.Sends.Should().HaveCount(minimumOccurrences ? 1 : 2);
        }
        finally { await db.DisposeAsync(); await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task SystemAlertDeliveredEpisode_DoesNotRenewCooldownForLaterEvent()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var workflow = SeedWorkflow(db);
            var firstId = SeedExecution(db, workflow, ExecutionStatus.Failed, DateTime.UtcNow.AddMinutes(-1));
            var policy = SystemEventPolicy(cooldownMinutes: 1);
            db.NotificationRules.Add(policy);
            await db.SaveChangesAsync();
            var sink = new RecordingSink(NotificationChannel.Email);
            var dispatcher = SystemEventDispatcher(factory, sink);
            await dispatcher.DispatchOnceAsync(TestContext.Current.CancellationToken);
            sink.Sends.Should().ContainSingle();
            await db.NotificationSuppressionStates.Where(s => s.NotificationRuleId == policy.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastFiredAt, DateTime.UtcNow.AddMinutes(-2)));
            var secondId = SeedExecution(db, workflow, ExecutionStatus.Failed, DateTime.UtcNow);

            await dispatcher.DispatchOnceAsync(TestContext.Current.CancellationToken);

            sink.Sends.Select(s => s.ctx.SourceKey).Should().BeEquivalentTo(
                [firstId.ToString("N"), secondId.ToString("N")],
                "an already delivered episode must not restart a finished cooldown");
        }
        finally { await db.DisposeAsync(); await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task SystemAlertMinOccurrences_CountsDistinctEpisodesAcrossDispatcherRestarts()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var workflow = SeedWorkflow(db);
            SeedExecution(db, workflow, ExecutionStatus.Failed, DateTime.UtcNow.AddMinutes(-1));
            var policy = SystemEventPolicy(minOccurrences: 3);
            db.NotificationRules.Add(policy);
            await db.SaveChangesAsync();
            var sink = new RecordingSink(NotificationChannel.Email);
            for (var pass = 0; pass < 3; pass++)
                await SystemEventDispatcher(factory, sink).DispatchOnceAsync(TestContext.Current.CancellationToken);
            sink.Sends.Should().BeEmpty("sampling one retained event repeatedly is one occurrence");
            (await db.NotificationSuppressionStates.AsNoTracking().SingleAsync()).OccurrenceCount.Should().Be(1);
            SeedExecution(db, workflow, ExecutionStatus.Failed, DateTime.UtcNow.AddSeconds(-1));
            await SystemEventDispatcher(factory, sink).DispatchOnceAsync(TestContext.Current.CancellationToken);
            sink.Sends.Should().BeEmpty();
            var thirdId = SeedExecution(db, workflow, ExecutionStatus.Failed, DateTime.UtcNow);

            await SystemEventDispatcher(factory, sink).DispatchOnceAsync(TestContext.Current.CancellationToken);

            sink.Sends.Should().ContainSingle().Which.ctx.SourceKey.Should().Be(thirdId.ToString("N"));
        }
        finally { await db.DisposeAsync(); await conn.DisposeAsync(); }
    }

    private static NotificationRule SystemEventPolicy(int cooldownMinutes = 0, int minOccurrences = 1) => new()
    {
        Id = Guid.NewGuid(), Name = "distinct-events", IsEnabled = true,
        Kind = NotificationRuleKind.System, EventTypes = "SystemAlert", SystemSourceId = "execution-result",
        ScopeKind = NotificationScopeKind.Global, DedupKeyTemplate = "{{workflowId}}",
        CooldownMinutes = cooldownMinutes, MinOccurrences = minOccurrences, OccurrenceWindowMinutes = 10,
        Routes = [Route(NotificationChannel.Email, "a@x")],
    };

    private static NotificationDispatcher SystemEventDispatcher(IServiceScopeFactory factory, INotificationSink sink) => new(
        factory, new SingleNodeClusterStateProvider(), [sink], new SystemAlertCatalog([new ExecutionResultSource()]),
        new ConfigurationBuilder().Build(), NullLogger<NotificationDispatcher>.Instance, TestDatabaseAvailability.Available);

    [Theory]
    [InlineData("execution-result", false)]
    [InlineData("audit-event", false)]
    [InlineData("execution-result", true)]
    [InlineData("audit-event", true)]
    public async Task SystemAlertRetry_RecoversRetainedEventOutsideLookback(string sourceId, bool deleted)
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            ISystemAlertSource source = sourceId == "execution-result" ? new ExecutionResultSource() : new AuditEventSource();
            var eventId = Guid.NewGuid();
            if (sourceId == "execution-result")
                eventId = SeedExecution(db, SeedWorkflow(db), ExecutionStatus.Failed, DateTime.UtcNow);
            else
                db.AuditLog.Add(new AuditLogEntry { Id = eventId, Action = "LOGIN_FAILED", Timestamp = DateTime.UtcNow });
            db.NotificationRules.Add(new NotificationRule
            {
                Id = Guid.NewGuid(), Name = "retained-event", IsEnabled = true,
                Kind = NotificationRuleKind.System, EventTypes = "SystemAlert", SystemSourceId = sourceId,
                ScopeKind = NotificationScopeKind.Global, SeverityOverride = NotificationSeverity.Critical,
                Routes = [Route(NotificationChannel.Email, "a@x")],
            });
            await db.SaveChangesAsync();
            var sink = new RecordingSink(NotificationChannel.Email) { Behavior = () => NotificationSendResult.Fail("temporary outage") };
            var clock = new ManualClock(DateTimeOffset.UtcNow);
            NotificationDispatcher Dispatcher() => new(factory, new SingleNodeClusterStateProvider(), [sink],
                new SystemAlertCatalog([source]), new ConfigurationBuilder().Build(),
                NullLogger<NotificationDispatcher>.Instance, TestDatabaseAvailability.Available)
            { Clock = clock };
            await Dispatcher().DispatchOnceAsync(TestContext.Current.CancellationToken);
            var pending = await db.NotificationDeliveryAttempts.AsNoTracking().SingleAsync();
            pending.Status.Should().Be(NotificationDeliveryStatus.Pending);

            var old = DateTime.UtcNow.AddHours(-1);
            if (sourceId == "execution-result")
            {
                if (deleted) await db.WorkflowExecutions.Where(e => e.Id == eventId).ExecuteDeleteAsync();
                else await db.WorkflowExecutions.Where(e => e.Id == eventId)
                    .ExecuteUpdateAsync(s => s.SetProperty(e => e.CompletedAt, old).SetProperty(e => e.StartedAt, old.AddSeconds(-1)));
            }
            else
            {
                if (deleted) await db.AuditLog.Where(e => e.Id == eventId).ExecuteDeleteAsync();
                else await db.AuditLog.Where(e => e.Id == eventId).ExecuteUpdateAsync(s => s.SetProperty(e => e.Timestamp, old));
            }
            (await source.ObserveAsync(db, SystemAlertQuery.Empty, TestContext.Current.CancellationToken)).Should().BeEmpty(
                "normal sampling must retain its lookback limit");
            sink.Behavior = () => NotificationSendResult.Ok;
            clock.Advance(TimeSpan.FromSeconds(31)); // past the first retry backoff

            await Dispatcher().DispatchOnceAsync(TestContext.Current.CancellationToken);

            var recovered = await db.NotificationDeliveryAttempts.AsNoTracking().SingleAsync();
            recovered.Id.Should().Be(pending.Id);
            recovered.Status.Should().Be(deleted ? NotificationDeliveryStatus.Failed : NotificationDeliveryStatus.Sent);
            sink.Sends.Should().HaveCount(deleted ? 1 : 2);
            if (!deleted)
            {
                sink.Sends[1].ctx.EventKey.Should().Be(pending.EventKey);
                sink.Sends[1].ctx.SourceKey.Should().Be(eventId.ToString("N"));
                sink.Sends[1].ctx.Severity.Should().Be(NotificationSeverity.Critical);
            }
        }
        finally { await db.DisposeAsync(); await conn.DisposeAsync(); }
    }

    [Theory]
    [InlineData(10)]
    [InlineData(1)]
    public async Task SystemAlertRetry_PreservesPolicyQueryAndSeverity(int ageMinutes)
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var workflow = SeedWorkflow(db);
            SeedExecution(db, workflow, ExecutionStatus.Failed, DateTime.UtcNow.AddMinutes(-ageMinutes));
            var rule = new NotificationRule
            {
                Id = Guid.NewGuid(), Name = "retry-extended-window", IsEnabled = true,
                Kind = NotificationRuleKind.System, EventTypes = "SystemAlert",
                SystemSourceId = "execution-result", SourceParametersJson = "{\"lookbackSeconds\":3600}",
                ScopeKind = NotificationScopeKind.Global, SeverityOverride = NotificationSeverity.Critical,
                FilterExpressionJson = SystemAlertConditions.Compare("status", "==", "Failed"),
                Routes = [Route(NotificationChannel.Email, "a@x")],
            };
            db.NotificationRules.Add(rule);
            await db.SaveChangesAsync();
            var sink = new RecordingSink(NotificationChannel.Email) { Behavior = () => NotificationSendResult.Fail("temporary transport error") };
            var clock = new ManualClock(DateTimeOffset.UtcNow);
            var dispatcher = new NotificationDispatcher(factory, new SingleNodeClusterStateProvider(), [sink],
                new SystemAlertCatalog([new ExecutionResultSource()]), new ConfigurationBuilder().Build(),
                NullLogger<NotificationDispatcher>.Instance, TestDatabaseAvailability.Available)
            { Clock = clock };

            await dispatcher.DispatchOnceAsync(TestContext.Current.CancellationToken);
            sink.Sends.Should().ContainSingle();
            (await db.NotificationDeliveryAttempts.AsNoTracking().SingleAsync()).Status.Should().Be(NotificationDeliveryStatus.Pending);
            sink.Behavior = () => NotificationSendResult.Ok;
            clock.Advance(TimeSpan.FromSeconds(31));

            await dispatcher.DispatchOnceAsync(TestContext.Current.CancellationToken);

            sink.Sends.Should().HaveCount(2, "the configured one-hour lookback still includes the existing execution");
            sink.Sends.Should().OnlyContain(s => s.ctx.Severity == NotificationSeverity.Critical);
            (await db.NotificationDeliveryAttempts.AsNoTracking().SingleAsync()).Status.Should().Be(NotificationDeliveryStatus.Sent);
        }
        finally { await db.DisposeAsync(); await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task PendingRetry_AfterRuleDisabled_DoesNotSend()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var workflow = SeedWorkflow(db);
            SeedInitialization(db, DateTime.UtcNow.AddHours(-1));
            var ruleId = SeedRule(db, "ExecutionFailed", Route(NotificationChannel.Email, "a@x"));
            SeedExecution(db, workflow, ExecutionStatus.Failed, DateTime.UtcNow);
            var sink = new RecordingSink(NotificationChannel.Email)
            {
                Behavior = () => NotificationSendResult.Fail("transport unavailable"),
            };
            var clock = new ManualClock(DateTimeOffset.UtcNow);
            var dispatcher = Build(factory, sink);
            dispatcher.Clock = clock;
            await dispatcher.DispatchOnceAsync(CancellationToken.None);
            sink.Sends.Should().ContainSingle();
            await db.NotificationRules.Where(r => r.Id == ruleId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsEnabled, false));
            clock.Advance(TimeSpan.FromMinutes(1));

            await dispatcher.DispatchOnceAsync(CancellationToken.None);

            sink.Sends.Should().ContainSingle("the rule master switch also prevents external retry sends");
            var attempt = await db.NotificationDeliveryAttempts.AsNoTracking().SingleAsync();
            attempt.Status.Should().Be(NotificationDeliveryStatus.Failed);
            attempt.Error.Should().Be("notification rule was disabled before retry");
        }
        finally { await db.DisposeAsync(); await conn.DisposeAsync(); }
    }

    [Theory]
    [InlineData(ExecutionStatus.Pending, "ExecutionQueuedLong")]
    [InlineData(ExecutionStatus.Running, "ExecutionRunningLong")]
    public async Task ElapsedAlerts_AdvancePastAlreadyDeliveredOldestBatch(ExecutionStatus status, string eventType)
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var workflow = SeedWorkflow(db);
            SeedRule(db, eventType, Route(NotificationChannel.Email, "a@x"));
            var startedAt = DateTime.UtcNow.AddHours(-2);
            for (var i = 0; i < 205; i++) db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = Guid.NewGuid(), WorkflowId = workflow, Status = status,
                StartedAt = startedAt.AddSeconds(i),
            });
            await db.SaveChangesAsync();
            var sink = new RecordingSink(NotificationChannel.Email);
            var dispatcher = Build(factory, sink);

            for (var pass = 0; pass < 3; pass++) await dispatcher.DispatchOnceAsync(CancellationToken.None);

            sink.Sends.Select(s => s.ctx.ExecutionId).Distinct().Should().HaveCount(205,
                "persistent old pending/running rows must not starve later elapsed alerts");
        }
        finally { await db.DisposeAsync(); await conn.DisposeAsync(); }
    }

    private static byte[] Key()
    {
        var k = new byte[32];
        for (var i = 0; i < k.Length; i++) k[i] = (byte)(i + 3);
        return k;
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
        => Build(factory, new SingleNodeClusterStateProvider(), sinks);

    private static NotificationDispatcher Build(
        IServiceScopeFactory factory,
        IClusterStateProvider cluster,
        params INotificationSink[] sinks)
    {
        var d = new NotificationDispatcher(
            factory, cluster, sinks,
            new NodePilot.Scheduler.SystemAlerts.SystemAlertCatalog(Array.Empty<NodePilot.Scheduler.SystemAlerts.ISystemAlertSource>()),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<NotificationDispatcher>.Instance,
            NodePilot.TestCommons.TestDatabaseAvailability.Available);
        return d;
    }

    private static NotificationDispatcher BuildWithConfig(
        IServiceScopeFactory factory, Microsoft.Extensions.Configuration.IConfiguration config, params INotificationSink[] sinks)
    {
        var d = new NotificationDispatcher(
            factory, new SingleNodeClusterStateProvider(), sinks,
            new NodePilot.Scheduler.SystemAlerts.SystemAlertCatalog(Array.Empty<NodePilot.Scheduler.SystemAlerts.ISystemAlertSource>()),
            config, NullLogger<NotificationDispatcher>.Instance,
            NodePilot.TestCommons.TestDatabaseAvailability.Available);
        return d;
    }

    [Fact]
    public async Task TerminalExecutionMatchingNoRule_IsStillDurablyCollected()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var workflow = SeedWorkflow(db);
            SeedInitialization(db, DateTime.UtcNow);
            SeedRule(db, "ExecutionSucceeded", Route(NotificationChannel.Email, "a@x"));
            SeedExecution(db, workflow, ExecutionStatus.Failed, DateTime.UtcNow);
            var sink = new RecordingSink(NotificationChannel.Email);
            (await Build(factory, sink).DispatchOnceAsync(CancellationToken.None)).Should().Be(0);
            (await db.WorkflowExecutions.AsNoTracking().SingleAsync()).TerminalNotificationCollected.Should().BeTrue();
            (await db.NotificationDeliveryAttempts.CountAsync()).Should().Be(0);
        }
        finally { await db.DisposeAsync(); await conn.DisposeAsync(); }
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

    private static void SeedInitialization(NodePilotDbContext db, DateTime lastSeen)
    {
        db.NotificationDispatcherStates.Add(new NotificationDispatcherState
        {
            Id = NotificationDispatcherState.SingletonId,
            InitializedAt = lastSeen,
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
    public async Task FirstRun_MarksVisibleHistoryCollected_DoesNotBackAlertHistory()
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
            email.Sends.Should().BeEmpty("first run marks existing terminal history collected");
            (await db.NotificationDispatcherStates.CountAsync()).Should().Be(1);
            (await db.NotificationDeliveryAttempts.CountAsync()).Should().Be(0);
            (await db.WorkflowExecutions.AsNoTracking().SingleAsync()).TerminalNotificationCollected.Should().BeTrue();

            SeedExecution(db, wf, ExecutionStatus.Failed, DateTime.UtcNow.AddMinutes(-10));
            (await Build(factory, email).DispatchOnceAsync(CancellationToken.None)).Should().Be(1,
                "a later commit remains eligible even when its CompletedAt predates initialization");
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
            SeedInitialization(db, DateTime.UtcNow.AddHours(-1));
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
            SeedInitialization(db, DateTime.UtcNow.AddHours(-1));
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
            SeedInitialization(db, DateTime.UtcNow.AddHours(-1));
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
    public async Task Rescan_AfterCollectionFlagReset_DoesNotDoubleSend()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            SeedInitialization(db, DateTime.UtcNow.AddHours(-1));
            SeedRule(db, "ExecutionFailed", Route(NotificationChannel.Email, "a@x"));
            SeedExecution(db, wf, ExecutionStatus.Failed, DateTime.UtcNow);
            var email = new RecordingSink(NotificationChannel.Email);
            var dispatcher = Build(factory, email);

            await dispatcher.DispatchOnceAsync(CancellationToken.None);
            // Delivery keys remain idempotent even if an execution is explicitly recollected.
            var execution = await db.WorkflowExecutions.SingleAsync();
            execution.TerminalNotificationCollected = false;
            db.Entry(execution).Property(e => e.TerminalNotificationCollected).IsModified = true;
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
            SeedInitialization(db, DateTime.UtcNow.AddHours(-1));
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
            SeedInitialization(db, DateTime.UtcNow.AddHours(-1));
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
            SeedInitialization(db, DateTime.UtcNow.AddHours(-1));
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
    public async Task TransientFailure_RetriedOnFirstDuePass_ThenSucceeds()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            SeedInitialization(db, DateTime.UtcNow.AddHours(-1));
            SeedRule(db, "ExecutionFailed", Route(NotificationChannel.Email, "a@x"));
            SeedExecution(db, wf, ExecutionStatus.Failed, DateTime.UtcNow);
            var calls = 0;
            var email = new RecordingSink(NotificationChannel.Email)
            {
                Behavior = () => ++calls == 1 ? NotificationSendResult.Fail("smtp down") : NotificationSendResult.Ok,
            };
            var clock = new ManualClock(DateTimeOffset.UtcNow);
            var dispatcher = Build(factory, email);
            dispatcher.Clock = clock;

            await dispatcher.DispatchOnceAsync(CancellationToken.None); // first send fails -> stays Pending
            (await db.NotificationDeliveryAttempts.AsNoTracking().SingleAsync()).Status
                .Should().Be(NotificationDeliveryStatus.Pending, "a transient failure must remain retryable, not be marked Failed");
            clock.Advance(TimeSpan.FromSeconds(31));

            await dispatcher.DispatchOnceAsync(CancellationToken.None); // recovery retries -> succeeds

            email.Sends.Should().HaveCount(2);
            (await db.NotificationDeliveryAttempts.AsNoTracking().SingleAsync()).Status.Should().Be(NotificationDeliveryStatus.Sent);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task LeaseEpochChangesDuringSend_DoesNotStartAnotherExternalSend()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            SeedInitialization(db, DateTime.UtcNow.AddHours(-1));
            SeedRule(db, "ExecutionFailed",
                Route(NotificationChannel.Email, "first@x", order: 0),
                Route(NotificationChannel.Email, "second@x", order: 1));
            SeedExecution(db, wf, ExecutionStatus.Failed, DateTime.UtcNow);
            var cluster = new MutableClusterState();
            var first = true;
            var email = new RecordingSink(NotificationChannel.Email)
            {
                BeforeSend = () =>
                {
                    if (!first) return;
                    first = false;
                    cluster.ReacquireWithNextEpoch();
                },
            };

            await Build(factory, cluster, email).DispatchOnceAsync(CancellationToken.None);

            email.Sends.Should().ContainSingle(
                "the old leader may finish the already-started send but must start nothing under a stale epoch");
            var attempts = await db.NotificationDeliveryAttempts.AsNoTracking()
                .OrderBy(attempt => attempt.CreatedAt)
                .ToListAsync();
            attempts.Should().ContainSingle(attempt => attempt.Status == NotificationDeliveryStatus.Sent);
            attempts.Should().ContainSingle(attempt => attempt.Status == NotificationDeliveryStatus.Pending);
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
            SeedInitialization(db, DateTime.UtcNow.AddHours(-1));
            SeedRule(db, "ExecutionFailed", Route(NotificationChannel.Email, "a@x"));
            SeedExecution(db, wf, ExecutionStatus.Failed, DateTime.UtcNow);
            var email = new RecordingSink(NotificationChannel.Email) { Behavior = () => NotificationSendResult.Fail("down") };
            var clock = new ManualClock(DateTimeOffset.UtcNow);
            var dispatcher = Build(factory, email);
            dispatcher.Clock = clock;

            for (var i = 0; i < 8; i++)
            {
                await dispatcher.DispatchOnceAsync(CancellationToken.None);
                clock.Advance(TimeSpan.FromHours(1)); // past every backoff step
            }

            email.Sends.Should().HaveCount(5, "bounded retry caps at MaxAttempts=5 then stops re-queuing");
            (await db.NotificationDeliveryAttempts.AsNoTracking().SingleAsync()).Status.Should().Be(NotificationDeliveryStatus.Failed);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task RecoverPending_BeforeBackoffElapsed_DoesNotRetry_ThenRetriesWhenDue()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            SeedInitialization(db, DateTime.UtcNow.AddHours(-1));
            SeedRule(db, "ExecutionFailed", Route(NotificationChannel.Email, "a@x"));
            SeedExecution(db, wf, ExecutionStatus.Failed, DateTime.UtcNow);
            var email = new RecordingSink(NotificationChannel.Email) { Behavior = () => NotificationSendResult.Fail("receiver down") };
            var clock = new ManualClock(DateTimeOffset.UtcNow);
            var dispatcher = Build(factory, email);
            dispatcher.Clock = clock;

            await dispatcher.DispatchOnceAsync(CancellationToken.None);
            email.Sends.Should().ContainSingle();

            clock.Advance(TimeSpan.FromSeconds(20));
            await dispatcher.DispatchOnceAsync(CancellationToken.None);
            email.Sends.Should().ContainSingle("the 30-second backoff after the first try has not elapsed");

            clock.Advance(TimeSpan.FromSeconds(11));
            await dispatcher.DispatchOnceAsync(CancellationToken.None);
            email.Sends.Should().HaveCount(2, "the first backoff has elapsed");

            clock.Advance(TimeSpan.FromSeconds(90));
            await dispatcher.DispatchOnceAsync(CancellationToken.None);
            email.Sends.Should().HaveCount(2, "the second backoff is two minutes");

            clock.Advance(TimeSpan.FromSeconds(31));
            await dispatcher.DispatchOnceAsync(CancellationToken.None);
            email.Sends.Should().HaveCount(3);
            var attempt = await db.NotificationDeliveryAttempts.AsNoTracking().SingleAsync();
            attempt.Status.Should().Be(NotificationDeliveryStatus.Pending);
            attempt.Attempt.Should().Be(3);
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
            SeedInitialization(db, DateTime.UtcNow.AddHours(-1));
            // A route whose stored cipher is not valid base64 -> GetRouteSecretAsync throws on
            // decrypt.
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
            SeedInitialization(db, DateTime.UtcNow.AddHours(-1));
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
    public async Task LateCommitWithOlderCompletionTime_IsCollectedOnNextPass()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var wf = SeedWorkflow(db);
            SeedInitialization(db, DateTime.UtcNow.AddHours(-1));
            SeedRule(db, "ExecutionFailed", Route(NotificationChannel.Email, "a@x"));
            SeedExecution(db, wf, ExecutionStatus.Failed, DateTime.UtcNow);
            var email = new RecordingSink(NotificationChannel.Email);
            var dispatcher = Build(factory, email);
            (await dispatcher.DispatchOnceAsync(CancellationToken.None)).Should().Be(1);

            SeedExecution(db, wf, ExecutionStatus.Failed, DateTime.UtcNow.AddMinutes(-5));
            await dispatcher.DispatchOnceAsync(CancellationToken.None);
            email.Sends.Should().HaveCount(2, "eligibility follows durable collection, not timestamp order");
            (await dispatcher.DispatchOnceAsync(CancellationToken.None)).Should().Be(0);
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
            SeedInitialization(db, DateTime.UtcNow.AddHours(-1));
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
            SeedInitialization(db, DateTime.UtcNow.AddHours(-1));
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

            // Pass 1: threshold 120s -> a 60s-old run is too young -> no alert.
            (await dispatcher.DispatchOnceAsync(CancellationToken.None)).Should().Be(0);
            email.Sends.Should().BeEmpty();

            // Operator lowers Alerting:LongRunningSeconds in the Settings UI -> config reload.
            memProvider.Set("Alerting:LongRunningSeconds", "10");

            // Same dispatcher instance: the next pass overlays the new 10s threshold -> 60s-old
            // run now qualifies -> exactly one alert (per-execution existence-check dedup).
            (await dispatcher.DispatchOnceAsync(CancellationToken.None)).Should().Be(1);
            email.Sends.Should().ContainSingle();
        }
        finally { conn.Dispose(); }
    }

    private sealed class ManualClock(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _now = utcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class MutableClusterState : IClusterStateProvider
    {
        public bool IsLeader => true;
        public string NodeId => "mutable";
        public DateTime? LeaseExpiresAt => DateTime.UtcNow.AddMinutes(1);
        public long LeaseEpoch { get; private set; } = 11;
        public DateTime? LastSuccessfulRenewAt => DateTime.UtcNow;
        public event Action<long>? OnLeadershipAcquired;
        public event Action? OnLeadershipLost;

        public void ReacquireWithNextEpoch()
        {
            OnLeadershipLost?.Invoke();
            LeaseEpoch++;
            OnLeadershipAcquired?.Invoke(LeaseEpoch);
        }
    }
}
