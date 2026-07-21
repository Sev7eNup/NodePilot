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
using NodePilot.Scheduler.SystemAlerts;
using Xunit;

namespace NodePilot.Engine.Tests.SystemAlerts;

/// <summary>
/// End-to-end: a System policy evaluated by the dispatcher reuses the delivery pipeline (persist-Pending →
/// send), fires exactly once per episode, and recovers silently — proving the system-alert evaluator
/// (the modular alert-source architecture from ADR-0008) is wired into the real dispatcher pass, not just
/// unit-correct in isolation.
/// </summary>
public class SystemAlertDispatcherTests
{
    private static byte[] Key()
    {
        var k = new byte[32];
        for (var i = 0; i < k.Length; i++) k[i] = (byte)(i + 7);
        return k;
    }

    private sealed class RecordingSink : INotificationSink
    {
        public NotificationChannel Channel => NotificationChannel.Email;
        public List<NotificationContext> Sends { get; } = [];
        public Task<NotificationSendResult> SendAsync(NotificationContext ctx, string target, string? secret, CancellationToken ct)
        {
            Sends.Add(ctx);
            return Task.FromResult(NotificationSendResult.Ok);
        }
    }

    private sealed class StubSource : ISystemAlertSource
    {
        public string SourceId => "stub";
        public Func<IReadOnlyList<SystemAlertObservation>> Observations { get; set; } = () => [];
        public SystemAlertSourceDescriptor Describe() => new(
            "stub", SystemAlertCategory.Queue, SystemAlertScopeCapability.GlobalOnly,
            NotificationSeverity.Warning, [], [], []);
        public Task<bool> IsAvailableAsync(NodePilotDbContext db, CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<SystemAlertObservation>> ObserveAsync(NodePilotDbContext db, SystemAlertQuery q, CancellationToken ct)
            => Task.FromResult(Observations());
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

    private static NotificationDispatcher Build(IServiceScopeFactory factory, StubSource source, RecordingSink sink)
        => new(factory, new SingleNodeClusterStateProvider(), new INotificationSink[] { sink },
            new SystemAlertCatalog([source]),
            new ConfigurationBuilder().Build(), NullLogger<NotificationDispatcher>.Instance);

    private static void SeedPolicy(NodePilotDbContext db, string filterJson)
    {
        var ruleId = Guid.NewGuid();
        db.NotificationRules.Add(new NotificationRule
        {
            Id = ruleId,
            Name = "backlog-critical",
            EventTypes = "SystemAlert",
            Kind = NotificationRuleKind.System,
            SystemSourceId = "stub",
            FilterExpressionJson = filterJson,
            Routes = [new NotificationRoute { Id = Guid.NewGuid(), NotificationRuleId = ruleId, Channel = NotificationChannel.Email, Target = "ops@x", Order = 0 }],
        });
        db.SaveChanges();
    }

    private static SystemAlertObservation Backlog(long depth) => new(
        "stub", "backlog", NotificationSeverity.Warning, "Backlog", "summary", "/executions",
        new Dictionary<string, object?> { ["depth"] = depth }, SignalValue: depth);

    [Fact]
    public async Task SystemPolicy_FiresOncePerEpisode_ThroughDeliveryPipeline()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            SeedPolicy(db, SystemAlertConditions.Compare("depth", ">", "500"));
            var source = new StubSource { Observations = () => [Backlog(600)] };
            var sink = new RecordingSink();
            var dispatcher = Build(factory, source, sink);

            await dispatcher.DispatchOnceAsync(CancellationToken.None);
            sink.Sends.Should().ContainSingle();
            sink.Sends[0].EventType.Should().Be(NotificationEventType.SystemAlert);
            sink.Sends[0].SourceId.Should().Be("stub");
            sink.Sends[0].SignalValue.Should().Be(600);

            db.ChangeTracker.Clear();
            var attempts = db.NotificationDeliveryAttempts.AsNoTracking().ToList();
            attempts.Should().ContainSingle().Which.Status.Should().Be(NotificationDeliveryStatus.Sent);

            // Second pass while the same episode is open → no new delivery (exactly-once guard).
            await dispatcher.DispatchOnceAsync(CancellationToken.None);
            sink.Sends.Should().HaveCount(1, "the open episode already delivered");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task SystemPolicy_BelowThreshold_DoesNotFire()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            SeedPolicy(db, SystemAlertConditions.Compare("depth", ">", "500"));
            var source = new StubSource { Observations = () => [Backlog(100)] };
            var sink = new RecordingSink();
            var dispatcher = Build(factory, source, sink);

            await dispatcher.DispatchOnceAsync(CancellationToken.None);

            sink.Sends.Should().BeEmpty();
        }
        finally { conn.Dispose(); }
    }
}
