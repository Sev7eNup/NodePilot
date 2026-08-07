using FluentAssertions;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Scheduler;
using NodePilot.Scheduler.SystemAlerts;
using NodePilot.Scheduler.SystemAlerts.Sources;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Engine.Tests.SystemAlerts;

/// <summary>
/// The one system-alert source backed by process memory instead of the database — it reports which
/// triggers the orchestrator currently cannot keep registered, a state no other source can see.
/// </summary>
public class TriggerUnhealthySourceTests
{
    private static Workflow Wf(Guid id, string name) => new() { Id = id, Name = name, DefinitionJson = "{}" };

    private static (TriggerHealthRegistry registry, TriggerUnhealthySource source) Subject()
    {
        var registry = new TriggerHealthRegistry();
        return (registry, new TriggerUnhealthySource(registry));
    }

    [Fact]
    public async Task IsAvailable_IsFalse_WhenEveryTriggerIsHealthy()
    {
        // An idle installation must not show the source as configurable-but-firing; the catalog
        // renders it unavailable, exactly like a source whose underlying feature is off.
        await using var db = TestDbFactory.Create();
        var (_, source) = Subject();

        (await source.IsAvailableAsync(db, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Observe_EmitsWorkflowScopedObservation_ForUnhealthyTrigger()
    {
        await using var db = TestDbFactory.Create();
        var wf = Wf(Guid.NewGuid(), "nightly-import");
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var (registry, source) = Subject();
        registry.MarkUnhealthy($"{wf.Id}:trg1", wf.Id, "trg1", "fileWatcherTrigger",
            "Win32Exception: network name deleted", consecutiveFailures: 3,
            DateTime.UtcNow.AddSeconds(-90));

        (await source.IsAvailableAsync(db, CancellationToken.None)).Should().BeTrue();
        var obs = await source.ObserveAsync(db, SystemAlertQuery.Empty, CancellationToken.None);

        var single = obs.Should().ContainSingle().Subject;
        single.SourceId.Should().Be("trigger-unhealthy");
        single.InstanceKey.Should().Be($"{wf.Id:N}:trg1");
        single.WorkflowId.Should().Be(wf.Id);
        single.WorkflowName.Should().Be("nightly-import");
        single.DeepLinkPath.Should().Be($"/workflows/{wf.Id:D}");
        ((long)single.Fields["unhealthySeconds"]!).Should().BeGreaterThanOrEqualTo(85);
        single.Fields["consecutiveFailures"].Should().Be(3L);
        single.Fields["triggerType"].Should().Be("fileWatcherTrigger");
        single.Summary.Should().Contain("network name deleted");
    }

    [Fact]
    public async Task Observe_KeepsOriginalSince_WhenRetriesKeepFailing()
    {
        // unhealthySeconds is what a policy alerts on; restamping it on every retry would pin it
        // near zero forever and no sustained-outage policy could ever fire.
        await using var db = TestDbFactory.Create();
        var wf = Wf(Guid.NewGuid(), "wf");
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var (registry, source) = Subject();
        var key = $"{wf.Id}:trg1";
        registry.MarkUnhealthy(key, wf.Id, "trg1", "fileWatcherTrigger", "first", 1, DateTime.UtcNow.AddMinutes(-10));
        registry.MarkUnhealthy(key, wf.Id, "trg1", "fileWatcherTrigger", "still failing", 5, DateTime.UtcNow);

        var single = (await source.ObserveAsync(db, SystemAlertQuery.Empty, CancellationToken.None))
            .Should().ContainSingle().Subject;

        ((long)single.Fields["unhealthySeconds"]!).Should().BeGreaterThanOrEqualTo(590);
        single.Fields["consecutiveFailures"].Should().Be(5L);
        single.Summary.Should().Contain("still failing");
    }

    [Fact]
    public async Task Observe_StillEmits_WhenWorkflowRowIsGone()
    {
        // The registry entry outlives a deleted workflow until the next sync pass clears it. The
        // observation must degrade to the id rather than throw or silently vanish.
        await using var db = TestDbFactory.Create();
        var (registry, source) = Subject();
        var ghost = Guid.NewGuid();
        registry.MarkUnhealthy($"{ghost}:trg1", ghost, "trg1", "databaseTrigger", "boom", 1, DateTime.UtcNow);

        var single = (await source.ObserveAsync(db, SystemAlertQuery.Empty, CancellationToken.None))
            .Should().ContainSingle().Subject;

        single.WorkflowName.Should().BeNull();
        single.Title.Should().Contain(ghost.ToString("D"));
    }

    [Fact]
    public async Task Observe_IsEmpty_AfterMarkHealthy()
    {
        await using var db = TestDbFactory.Create();
        var (registry, source) = Subject();
        var wfId = Guid.NewGuid();
        var key = $"{wfId}:trg1";
        registry.MarkUnhealthy(key, wfId, "trg1", "fileWatcherTrigger", "gone", 1, DateTime.UtcNow);

        registry.MarkHealthy(key);

        (await source.ObserveAsync(db, SystemAlertQuery.Empty, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public void Clear_DropsEverything()
    {
        // Called when this node stops owning triggers (leadership loss) — a follower must not
        // alert on the leader's triggers.
        var registry = new TriggerHealthRegistry();
        registry.MarkUnhealthy("a:1", Guid.NewGuid(), "1", "fileWatcherTrigger", "x", 1, DateTime.UtcNow);
        registry.MarkUnhealthy("b:1", Guid.NewGuid(), "1", "databaseTrigger", "y", 1, DateTime.UtcNow);

        registry.Clear();

        registry.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void Describe_DeclaresWorkflowScopeAndPreset()
    {
        var (_, source) = Subject();

        var descriptor = source.Describe();

        descriptor.SourceId.Should().Be("trigger-unhealthy");
        descriptor.Category.Should().Be(SystemAlertCategory.Health);
        descriptor.ScopeCapability.Should().Be(SystemAlertScopeCapability.WorkflowScoped);
        descriptor.Fields.Select(f => f.Name).Should()
            .BeEquivalentTo(["unhealthySeconds", "consecutiveFailures", "triggerType"]);
        descriptor.Presets.Should().ContainSingle().Which.PresetId.Should().Be("registration-failing");
    }
}
