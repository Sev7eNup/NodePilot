using FluentAssertions;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Scheduler.SystemAlerts;
using NodePilot.Scheduler.SystemAlerts.Sources;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Engine.Tests.SystemAlerts;

/// <summary>
/// The audit-log event source: one observation per audit row inside the lookback window, pre-filtered
/// server-side by the <c>actions</c> parameter, capped per pass, keyed by row id, with the attempted
/// username recovered from Details for anonymous login failures.
/// </summary>
public class AuditEventSourceTests
{
    private static readonly AuditEventSource Source = new();

    private static AuditLogEntry Row(string action, DateTime? at = null, string? username = null,
        string? details = null, string? ip = null, string? resourceType = null, Guid? resourceId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Timestamp = at ?? DateTime.UtcNow.AddSeconds(-10),
            Action = action,
            Username = username,
            Details = details,
            IpAddress = ip,
            ResourceType = resourceType,
            ResourceId = resourceId,
        };

    private static SystemAlertQuery Query(string? actions = null, int? lookbackSeconds = null)
    {
        var values = new Dictionary<string, object?>();
        if (actions is not null) values["actions"] = actions;
        if (lookbackSeconds is not null) values["lookbackSeconds"] = lookbackSeconds;
        return new SystemAlertQuery(values);
    }

    private static async Task<IReadOnlyList<SystemAlertObservation>> Observe(
        NodePilotDbContext db, SystemAlertQuery? query = null)
        => await Source.ObserveAsync(db, query ?? SystemAlertQuery.Empty, CancellationToken.None);

    [Fact]
    public void Describe_DeclaresSecurityCategory_GlobalOnly_FieldsParametersPresets()
    {
        var d = Source.Describe();

        d.SourceId.Should().Be("audit-event");
        d.Category.Should().Be(SystemAlertCategory.Security);
        d.ScopeCapability.Should().Be(SystemAlertScopeCapability.GlobalOnly);
        d.DefaultSeverity.Should().Be(NotificationSeverity.Warning);
        d.Fields.Select(f => f.Name).Should().Equal(
            "action", "outcome", "category", "username", "ipAddress", "resourceType", "details");
        d.Fields.Single(f => f.Name == "action").Type.Should().Be(SystemAlertFieldType.String,
            "the 157 codes live in AuditActions — an enum copy here would be a second list to forget");
        d.Parameters.Select(p => p.Name).Should().Equal("lookbackSeconds", "actions");
        d.Parameters.Single(p => p.Name == "lookbackSeconds").Max.Should().Be(AuditEventSource.MaxLookbackSeconds,
            "an unbounded window would let one sample read the whole audit log");
        d.Parameters.Single(p => p.Name == "actions").Required.Should().BeFalse();
        d.Presets.Select(p => p.PresetId).Should().Equal(
            "failed-login", "account-locked", "break-glass-login", "privileged-change");
        d.Presets.Should().OnlyContain(p => p.Parameters == null,
            "presets ship a condition only — a pre-filter that contradicts a later-edited condition never fires");
    }

    [Fact]
    public async Task IsAvailable_IsAlwaysTrue()
    {
        await using var db = TestDbFactory.Create();
        (await Source.IsAvailableAsync(db, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Observe_MapsRowToObservation_KeyedByRowId_OccurredAtTimestamp()
    {
        await using var db = TestDbFactory.Create();
        var at = DateTime.UtcNow.AddSeconds(-30);
        var resourceId = Guid.NewGuid();
        var row = Row(AuditActions.UserRoleChanged, at, username: "alice",
            details: "{\"role\":\"Admin\"}", ip: "10.0.0.5", resourceType: "User", resourceId: resourceId);
        db.AuditLog.Add(row);
        await db.SaveChangesAsync();

        var single = (await Observe(db)).Should().ContainSingle().Subject;

        single.SourceId.Should().Be("audit-event");
        single.InstanceKey.Should().Be(row.Id.ToString("N"), "the username must not leak into sourceKey / the event key header");
        single.OccurredAt.Should().Be(at);
        single.DeepLinkPath.Should().Be("/audit");
        single.WorkflowId.Should().BeNull();
        single.Title.Should().Be("Audit USER_ROLE_CHANGED: alice");
        single.Summary.Should().Contain("by alice").And.Contain("from 10.0.0.5")
            .And.Contain($"on User {resourceId:D}").And.Contain("{\"role\":\"Admin\"}");
        single.Fields["action"].Should().Be("USER_ROLE_CHANGED");
        single.Fields["outcome"].Should().Be("success");
        single.Fields["category"].Should().Be("iam");
        single.Fields["username"].Should().Be("alice");
        single.Fields["ipAddress"].Should().Be("10.0.0.5");
        single.Fields["resourceType"].Should().Be("User");
        single.Fields["details"].Should().Be("{\"role\":\"Admin\"}");
        single.SeveritySuggestion.Should().Be(NotificationSeverity.Info);
    }

    [Fact]
    public async Task Observe_UsernameFallsBackToDetails_ForAnonymousLoginFailure()
    {
        // LOGIN_FAILED is written by an unauthenticated request: the actor column is null and the
        // attempted name only exists in Details.username — exactly the field a failed-login policy needs.
        await using var db = TestDbFactory.Create();
        db.AuditLog.Add(Row(AuditActions.LoginFailed, username: null,
            details: "{\"username\":\"mallory\",\"reason\":\"invalid_password\"}", ip: "203.0.113.9"));
        await db.SaveChangesAsync();

        var single = (await Observe(db)).Should().ContainSingle().Subject;

        single.Fields["username"].Should().Be("mallory");
        single.Fields["outcome"].Should().Be("failure");
        single.Fields["category"].Should().Be("iam");
        single.Title.Should().Be("Audit LOGIN_FAILED: mallory");
        single.SeveritySuggestion.Should().Be(NotificationSeverity.Warning);
    }

    [Fact]
    public async Task Observe_NoActorAnywhere_ReportsSystem()
    {
        await using var db = TestDbFactory.Create();
        db.AuditLog.Add(Row(AuditActions.DatabaseRecovered, username: null, details: null));
        await db.SaveChangesAsync();

        var single = (await Observe(db)).Should().ContainSingle().Subject;

        single.Fields["username"].Should().Be("");
        single.Title.Should().Be("Audit DATABASE_RECOVERED: system");
        single.Summary.Should().NotContain(" from ").And.NotContain(" on ");
    }

    [Fact]
    public async Task Observe_MapsOutcome_FromSuffixAndDetails()
    {
        await using var db = TestDbFactory.Create();
        db.AuditLog.AddRange(
            Row(AuditActions.LoginLocked, at: DateTime.UtcNow.AddSeconds(-40)),
            Row(AuditActions.DbAdminSqlWriteAttempted, at: DateTime.UtcNow.AddSeconds(-30)),
            Row(AuditActions.WorkflowUpdated, at: DateTime.UtcNow.AddSeconds(-20), details: "{\"success\":false}"),
            Row(AuditActions.WorkflowPublished, at: DateTime.UtcNow.AddSeconds(-10)));
        await db.SaveChangesAsync();

        var obs = await Observe(db);

        obs.Select(o => (o.Fields["action"], o.Fields["outcome"], o.Fields["category"])).Should().Equal(
            ("LOGIN_LOCKED", "failure", "iam"),
            ("DBADMIN_SQL_WRITE_ATTEMPTED", "unknown", "configuration"),
            ("WORKFLOW_UPDATED", "failure", "configuration"),
            ("WORKFLOW_PUBLISHED", "success", "configuration"));
    }

    [Fact]
    public async Task Observe_ActionsParameter_FiltersServerSide_TrimsAndUppercases()
    {
        await using var db = TestDbFactory.Create();
        db.AuditLog.AddRange(
            Row(AuditActions.LoginFailed), Row(AuditActions.LoginLocked), Row(AuditActions.LoginSuccess),
            Row(AuditActions.UserRoleChanged));
        await db.SaveChangesAsync();

        var obs = await Observe(db, Query(actions: " login_failed , LOGIN_LOCKED,, "));

        obs.Select(o => (string)o.Fields["action"]!).Should().BeEquivalentTo("LOGIN_FAILED", "LOGIN_LOCKED");
    }

    [Fact]
    public async Task Observe_EmptyActions_SkipsHousekeepingCodes_ExplicitActionsIncludeThem()
    {
        await using var db = TestDbFactory.Create();
        db.AuditLog.AddRange(
            Row(AuditActions.CredentialDecrypted), Row(AuditActions.TokenRefreshed), Row(AuditActions.LoginSuccess));
        await db.SaveChangesAsync();

        var byDefault = await Observe(db);
        byDefault.Select(o => (string)o.Fields["action"]!).Should().Equal("LOGIN_SUCCESS");

        var explicitly = await Observe(db, Query(actions: "CREDENTIAL_DECRYPTED,TOKEN_REFRESHED"));
        explicitly.Select(o => (string)o.Fields["action"]!).Should().BeEquivalentTo("CREDENTIAL_DECRYPTED", "TOKEN_REFRESHED");
    }

    [Fact]
    public async Task Observe_LookbackParameter_BoundsTheWindow()
    {
        await using var db = TestDbFactory.Create();
        db.AuditLog.AddRange(
            Row(AuditActions.LoginFailed, at: DateTime.UtcNow.AddSeconds(-400)),
            Row(AuditActions.LoginFailed, at: DateTime.UtcNow.AddSeconds(-100)));
        await db.SaveChangesAsync();

        (await Observe(db)).Should().ContainSingle("the default 300 s window excludes the 400 s-old row");
        (await Observe(db, Query(lookbackSeconds: 600))).Should().HaveCount(2);
        (await Observe(db, Query(lookbackSeconds: 50))).Should().BeEmpty();
    }

    [Fact]
    public async Task Observe_ClampsLookbackToMax()
    {
        await using var db = TestDbFactory.Create();
        db.AuditLog.Add(Row(AuditActions.LoginFailed, at: DateTime.UtcNow.AddSeconds(-AuditEventSource.MaxLookbackSeconds - 60)));
        await db.SaveChangesAsync();

        (await Observe(db, Query(lookbackSeconds: int.MaxValue))).Should().BeEmpty(
            "a policy restored with an out-of-range value must not turn one sample into a full-table scan");
    }

    [Fact]
    public async Task Observe_HasNoRowCap_ReturnsEveryRowInWindow_OldestFirst()
    {
        // An oldest-first cap over a sliding window is a cliff, not a load guard: rows arriving faster than
        // the cap per dispatcher interval would age past the prefix and out of the window unobserved.
        await using var db = TestDbFactory.Create();
        var oldest = DateTime.UtcNow.AddSeconds(-250);
        for (var i = 0; i < 450; i++)
            db.AuditLog.Add(Row(AuditActions.WebhookTriggered, at: oldest.AddMilliseconds(i * 10)));
        await db.SaveChangesAsync();

        var obs = await Observe(db);

        obs.Should().HaveCount(450);
        obs.First().OccurredAt.Should().Be(oldest);
        obs.Select(o => o.OccurredAt).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Observe_SummaryTruncatesDetails_FieldKeepsFullJson()
    {
        await using var db = TestDbFactory.Create();
        var details = "{\"payload\":\"" + new string('x', 600) + "\"}";
        db.AuditLog.Add(Row(AuditActions.WorkflowUpdated, details: details));
        await db.SaveChangesAsync();

        var single = (await Observe(db)).Should().ContainSingle().Subject;

        single.Fields["details"].Should().Be(details, "contains-filters must see the whole redacted JSON");
        single.Summary.Should().NotContain(details).And.EndWith("…");
        single.Summary.Length.Should().BeLessThan(details.Length);
    }

    [Fact]
    public async Task Presets_MatchOnlyTheirIntendedRows()
    {
        await using var db = TestDbFactory.Create();
        db.AuditLog.AddRange(
            Row(AuditActions.LoginFailed, at: DateTime.UtcNow.AddSeconds(-50), details: "{\"username\":\"bob\"}"),
            Row(AuditActions.LoginLocked, at: DateTime.UtcNow.AddSeconds(-40)),
            Row(AuditActions.BreakGlassLoginSuccess, at: DateTime.UtcNow.AddSeconds(-30), username: "root"),
            Row(AuditActions.UserRoleChanged, at: DateTime.UtcNow.AddSeconds(-20), username: "admin"),
            Row(AuditActions.LoginSuccess, at: DateTime.UtcNow.AddSeconds(-10), username: "alice"));
        await db.SaveChangesAsync();
        var obs = await Observe(db);
        var presets = Source.Describe().Presets.ToDictionary(p => p.PresetId);

        string[] Matching(string presetId) => obs
            .Where(o => SystemAlertEvaluator.Matches(presets[presetId].ConditionJson, SystemAlertEvaluator.FieldMap(o)))
            .Select(o => (string)o.Fields["action"]!).ToArray();

        Matching("failed-login").Should().Equal("LOGIN_FAILED");
        Matching("account-locked").Should().Equal("LOGIN_LOCKED");
        Matching("break-glass-login").Should().Equal("BREAK_GLASS_LOGIN_SUCCESS");
        Matching("privileged-change").Should().Equal("USER_ROLE_CHANGED");
    }

    [Fact]
    public void ParseActions_SplitsTrimsUppercasesAndDedupes()
    {
        AuditEventSource.ParseActions(null).Should().BeEmpty();
        AuditEventSource.ParseActions("  ").Should().BeEmpty();
        AuditEventSource.ParseActions(" login_failed, LOGIN_FAILED ,, logout ").Should().Equal("LOGIN_FAILED", "LOGOUT");
    }
}
