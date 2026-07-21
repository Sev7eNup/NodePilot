using FluentAssertions;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Scheduler.SystemAlerts;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Engine.Tests.SystemAlerts;

/// <summary>
/// Exercises the SystemAlertEvaluator state machine: sustain windows, per-episode idempotent EventKeys,
/// silent recovery, the per-policy activation watermark, scope, severity override, and orphan pruning.
/// Uses a stub source so observations are controlled precisely and time is injected via the `now` arg.
/// </summary>
public class SystemAlertEvaluatorTests
{
    private const string SourceId = "stub";

    private sealed class StubSource : ISystemAlertSource
    {
        public string SourceId => SystemAlertEvaluatorTests.SourceId;
        public bool Available { get; set; } = true;
        public Func<IReadOnlyList<SystemAlertObservation>> Observations { get; set; } = () => [];
        public SystemAlertSourceDescriptor Describe() => new(
            SourceId, SystemAlertCategory.Queue, SystemAlertScopeCapability.WorkflowScoped,
            NotificationSeverity.Warning, [], [], []);
        public Task<bool> IsAvailableAsync(NodePilotDbContext db, CancellationToken ct) => Task.FromResult(Available);
        public Task<IReadOnlyList<SystemAlertObservation>> ObserveAsync(NodePilotDbContext db, SystemAlertQuery q, CancellationToken ct)
            => Task.FromResult(Observations());
    }

    private static (SystemAlertEvaluator ev, StubSource src) Build()
    {
        var src = new StubSource();
        return (new SystemAlertEvaluator(new SystemAlertCatalog([src])), src);
    }

    private static NotificationRule Policy(
        string? filter = null, int sustain = 0, NotificationScopeKind scope = NotificationScopeKind.Global,
        NotificationSeverity? severityOverride = null, DateTime? activatedAt = null, params NotificationRuleTarget[] targets)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = "p-" + Guid.NewGuid().ToString("N")[..8],
            EventTypes = "SystemAlert",
            Kind = NotificationRuleKind.System,
            SystemSourceId = SourceId,
            FilterExpressionJson = filter,
            SustainForSeconds = sustain,
            ScopeKind = scope,
            SeverityOverride = severityOverride,
            ActivatedAt = activatedAt,
            Targets = targets.ToList(),
        };

    private static SystemAlertObservation Obs(long depth, string instanceKey = "x", DateTime? occurredAt = null, Guid? workflowId = null)
        => new(SourceId, instanceKey, NotificationSeverity.Warning, "title", "summary", "/x",
            new Dictionary<string, object?> { ["depth"] = depth }, WorkflowId: workflowId, OccurredAt: occurredAt);

    private static readonly string DepthOver500 = SystemAlertConditions.Compare("depth", ">", "500");

    private static async Task<IReadOnlyList<SystemAlertFire>> Eval(SystemAlertEvaluator ev, NodePilotDbContext db, NotificationRule p, DateTime now)
    {
        var fires = await ev.EvaluateAsync(db, [p], now, CancellationToken.None);
        await db.SaveChangesAsync();
        return fires;
    }

    [Fact]
    public async Task Sustain0_FiresImmediately_WhenConditionHolds()
    {
        await using var db = TestDbFactory.Create();
        var (ev, src) = Build();
        src.Observations = () => [Obs(600)];
        var p = Policy(filter: DepthOver500);

        var fires = await Eval(ev, db, p, DateTime.UtcNow);

        fires.Should().ContainSingle();
        fires[0].Context.EventType.Should().Be(NotificationEventType.SystemAlert);
        fires[0].Context.SourceId.Should().Be(SourceId);
    }

    [Fact]
    public async Task ConditionFalse_DoesNotFire()
    {
        await using var db = TestDbFactory.Create();
        var (ev, src) = Build();
        src.Observations = () => [Obs(100)];

        var fires = await Eval(ev, db, Policy(filter: DepthOver500), DateTime.UtcNow);

        fires.Should().BeEmpty();
    }

    [Fact]
    public async Task Sustain_HoldsFireUntilWindowElapses()
    {
        await using var db = TestDbFactory.Create();
        var (ev, src) = Build();
        src.Observations = () => [Obs(600)];
        var p = Policy(filter: DepthOver500, sustain: 60);
        var t0 = DateTime.UtcNow;

        (await Eval(ev, db, p, t0)).Should().BeEmpty("condition just started holding");
        (await Eval(ev, db, p, t0.AddSeconds(30))).Should().BeEmpty("still inside the sustain window");
        (await Eval(ev, db, p, t0.AddSeconds(61))).Should().ContainSingle("sustain window elapsed");
    }

    [Fact]
    public async Task OpenEpisode_ReemitsSameEventKeyEachPass_ForIdempotentDelivery()
    {
        await using var db = TestDbFactory.Create();
        var (ev, src) = Build();
        src.Observations = () => [Obs(600)];
        var p = Policy(filter: DepthOver500);
        var t0 = DateTime.UtcNow;

        var first = await Eval(ev, db, p, t0);
        var second = await Eval(ev, db, p, t0.AddSeconds(30));

        first.Should().ContainSingle();
        second.Should().ContainSingle();
        second[0].Context.EventKey.Should().Be(first[0].Context.EventKey,
            "an open episode keeps its EventKey so the (rule,route,eventKey) guard dedups");
    }

    [Fact]
    public async Task Recovery_EndsEpisodeSilently_ThenNewEpisodeGetsNewEventKey()
    {
        await using var db = TestDbFactory.Create();
        var (ev, src) = Build();
        var p = Policy(filter: DepthOver500);
        var t0 = DateTime.UtcNow;

        src.Observations = () => [Obs(600)];
        var first = await Eval(ev, db, p, t0);

        src.Observations = () => [Obs(100)];
        var recovered = await Eval(ev, db, p, t0.AddSeconds(30));
        recovered.Should().BeEmpty("condition no longer holds — episode ends, no resolved alert");

        src.Observations = () => [Obs(600)];
        var second = await Eval(ev, db, p, t0.AddSeconds(60));
        second.Should().ContainSingle();
        second[0].Context.EventKey.Should().NotBe(first[0].Context.EventKey, "a re-opened episode is a new episode");
    }

    [Fact]
    public async Task ActivationWatermark_SkipsEventsBeforeActivation()
    {
        await using var db = TestDbFactory.Create();
        var (ev, src) = Build();
        var activatedAt = DateTime.UtcNow;
        // one event before activation (must be skipped), one after (must fire)
        src.Observations = () =>
        [
            Obs(600, instanceKey: "old", occurredAt: activatedAt.AddSeconds(-30)),
            Obs(600, instanceKey: "new", occurredAt: activatedAt.AddSeconds(30)),
        ];
        var p = Policy(filter: DepthOver500, activatedAt: activatedAt);

        var fires = await Eval(ev, db, p, activatedAt.AddSeconds(60));

        fires.Should().ContainSingle("only the post-activation event alerts");
        fires[0].Context.SourceKey.Should().Be("new");
    }

    [Fact]
    public async Task Scope_Workflows_OnlyFiresForTargetedWorkflows()
    {
        await using var db = TestDbFactory.Create();
        var (ev, src) = Build();
        var targeted = Guid.NewGuid();
        var other = Guid.NewGuid();
        src.Observations = () =>
        [
            Obs(600, instanceKey: "a", workflowId: targeted),
            Obs(600, instanceKey: "b", workflowId: other),
        ];
        var p = Policy(filter: DepthOver500, scope: NotificationScopeKind.Workflows,
            targets: new NotificationRuleTarget { Id = Guid.NewGuid(), TargetKind = NotificationTargetKind.Workflow, TargetId = targeted });

        var fires = await Eval(ev, db, p, DateTime.UtcNow);

        fires.Should().ContainSingle();
        fires[0].Context.WorkflowId.Should().Be(targeted);
    }

    [Fact]
    public async Task SeverityOverride_ReplacesObservationSeverity()
    {
        await using var db = TestDbFactory.Create();
        var (ev, src) = Build();
        src.Observations = () => [Obs(600)];
        var p = Policy(filter: DepthOver500, severityOverride: NotificationSeverity.Critical);

        var fires = await Eval(ev, db, p, DateTime.UtcNow);

        fires[0].Context.Severity.Should().Be(NotificationSeverity.Critical);
    }

    [Fact]
    public async Task EmptyFilter_AlwaysMatches()
    {
        await using var db = TestDbFactory.Create();
        var (ev, src) = Build();
        src.Observations = () => [Obs(1)];

        var fires = await Eval(ev, db, Policy(filter: null), DateTime.UtcNow);

        fires.Should().ContainSingle("a policy with no filter alerts on every observation");
    }

    [Fact]
    public async Task UnavailableSource_YieldsNoFires()
    {
        await using var db = TestDbFactory.Create();
        var (ev, src) = Build();
        src.Available = false;
        src.Observations = () => [Obs(600)];

        var fires = await Eval(ev, db, Policy(filter: DepthOver500), DateTime.UtcNow);

        fires.Should().BeEmpty();
    }

    [Fact]
    public async Task MissingSource_DoesNotThrow()
    {
        await using var db = TestDbFactory.Create();
        var ev = new SystemAlertEvaluator(new SystemAlertCatalog([]));
        var p = Policy(filter: DepthOver500); // references "stub", which isn't registered

        var fires = await ev.EvaluateAsync(db, [p], DateTime.UtcNow, CancellationToken.None);

        fires.Should().BeEmpty();
    }

    [Fact]
    public async Task PruneOrphanedState_DeletesStateForNonEnabledPolicies()
    {
        await using var db = TestDbFactory.Create();
        var (ev, _) = Build();
        var keep = Guid.NewGuid();
        var drop = Guid.NewGuid();
        db.SystemAlertPolicyStates.AddRange(
            new SystemAlertPolicyState { Id = Guid.NewGuid(), NotificationRuleId = keep, SourceId = SourceId, InstanceKey = "x" },
            new SystemAlertPolicyState { Id = Guid.NewGuid(), NotificationRuleId = drop, SourceId = SourceId, InstanceKey = "y" });
        await db.SaveChangesAsync();

        await ev.PruneOrphanedStateAsync(db, [keep], CancellationToken.None);

        var remaining = db.SystemAlertPolicyStates.Select(s => s.NotificationRuleId).ToList();
        remaining.Should().Equal(keep);
    }
}
