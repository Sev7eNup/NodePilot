using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Data.Tests;

/// <summary>
/// Verifies the additive system-alert-policy schema (ADR 0008 — the design record for the built-in
/// system-alert-policy feature, migration AddSystemAlertPolicies): the new
/// NotificationRule columns default/round-trip, existing custom rules stay Custom, and the two new state
/// tables enforce their unique indexes.
/// </summary>
public class SystemAlertPolicySchemaTests
{
    private static NotificationRule CustomRule(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        EventTypes = "ExecutionFailed",
    };

    [Fact]
    public async Task NotificationRule_DefaultsToCustomKind_WhenUnset()
    {
        await using var db = TestDbFactory.Create();
        db.NotificationRules.Add(CustomRule("legacy"));
        await db.SaveChangesAsync();

        var loaded = await db.NotificationRules.AsNoTracking().SingleAsync();
        loaded.Kind.Should().Be(NotificationRuleKind.Custom);
        loaded.SystemSourceId.Should().BeNull();
        loaded.SustainForSeconds.Should().Be(0);
        loaded.SeverityOverride.Should().BeNull();
    }

    [Fact]
    public async Task NotificationRule_SystemPolicyFields_RoundTrip()
    {
        await using var db = TestDbFactory.Create();
        var activatedAt = new DateTime(2026, 7, 10, 8, 0, 0, DateTimeKind.Utc);
        db.NotificationRules.Add(new NotificationRule
        {
            Id = Guid.NewGuid(),
            Name = "backlog-critical",
            EventTypes = "SystemAlert",
            Kind = NotificationRuleKind.System,
            SystemSourceId = "backlog",
            SystemPresetId = "high",
            SourceParametersJson = """{"lookbackSeconds":300}""",
            SustainForSeconds = 120,
            SeverityOverride = NotificationSeverity.Critical,
            ActivatedAt = activatedAt,
        });
        await db.SaveChangesAsync();

        var loaded = await db.NotificationRules.AsNoTracking().SingleAsync(r => r.Kind == NotificationRuleKind.System);
        loaded.SystemSourceId.Should().Be("backlog");
        loaded.SystemPresetId.Should().Be("high");
        loaded.SourceParametersJson.Should().Be("""{"lookbackSeconds":300}""");
        loaded.SustainForSeconds.Should().Be(120);
        loaded.SeverityOverride.Should().Be(NotificationSeverity.Critical);
        loaded.ActivatedAt.Should().Be(activatedAt);
    }

    [Fact]
    public async Task SystemAlertPolicyState_IsUnique_PerRuleSourceInstance()
    {
        await using var db = TestDbFactory.Create();
        var ruleId = Guid.NewGuid();
        db.SystemAlertPolicyStates.Add(new SystemAlertPolicyState { Id = Guid.NewGuid(), NotificationRuleId = ruleId, SourceId = "backlog", InstanceKey = "backlog" });
        await db.SaveChangesAsync();

        db.SystemAlertPolicyStates.Add(new SystemAlertPolicyState { Id = Guid.NewGuid(), NotificationRuleId = ruleId, SourceId = "backlog", InstanceKey = "backlog" });
        var act = async () => await db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task SystemAlertSourceState_IsUnique_PerSourceStateKey()
    {
        await using var db = TestDbFactory.Create();
        db.SystemAlertSourceStates.Add(new SystemAlertSourceState { Id = Guid.NewGuid(), SourceId = "execution-result", StateKey = "" });
        await db.SaveChangesAsync();

        db.SystemAlertSourceStates.Add(new SystemAlertSourceState { Id = Guid.NewGuid(), SourceId = "execution-result", StateKey = "" });
        var act = async () => await db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }
}
