using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Security;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public class AlertingControllerTests
{
    [Fact]
    public void TestFire_UsesHeavyAlertingRateLimit()
    {
        var attribute = typeof(AlertingController)
            .GetMethod(nameof(AlertingController.TestFire))!
            .GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: true)
            .Cast<EnableRateLimitingAttribute>()
            .Single();

        attribute.PolicyName.Should().Be("alerting-heavy");
    }
    private static byte[] Key()
    {
        var k = new byte[32];
        for (var i = 0; i < k.Length; i++) k[i] = (byte)(i + 3);
        return k;
    }

    private sealed class RecordingSink(NotificationChannel channel) : INotificationSink
    {
        public NotificationChannel Channel { get; } = channel;
        public List<(string target, string? secret)> Sends { get; } = [];
        public Func<NotificationSendResult>? Behavior { get; set; }
        public Task<NotificationSendResult> SendAsync(NotificationContext ctx, string target, string? secret, CancellationToken ct)
        {
            Sends.Add((target, secret));
            return Task.FromResult(Behavior?.Invoke() ?? NotificationSendResult.Ok);
        }
    }

    private sealed record Harness(AlertingController Ctrl, NotificationRuleStore Store, RecordingSink Email, RecordingSink Hook);

    private static Harness Build(NodePilotDbContext db)
    {
        var store = new NotificationRuleStore(db, new AesGcmSecretProtector(Key()));
        var email = new RecordingSink(NotificationChannel.Email);
        var hook = new RecordingSink(NotificationChannel.GenericWebhook);
        var ctrl = new AlertingController(store, db, NoopAuditWriter.Instance, new INotificationSink[] { email, hook });
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "Admin"), new Claim(ClaimTypes.Name, "admin")], "test"));
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
        return new Harness(ctrl, store, email, hook);
    }

    private static CreateNotificationRuleRequest Req(
        string name, string events = "ExecutionFailed", string scope = "Global", string? filter = null,
        IReadOnlyList<NotificationRouteDto>? routes = null, IReadOnlyList<NotificationRuleTargetDto>? targets = null,
        int cooldown = 0)
        => new(name, null, true, events.Split(','), filter, scope, cooldown, 1, 0,
            routes ?? [new NotificationRouteDto(null, "Email", "a@x", null, 0)], targets);

    private static async Task<NotificationRuleResponse> Create(AlertingController ctrl, CreateNotificationRuleRequest req)
    {
        var created = (await ctrl.Create(req, CancellationToken.None)).Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        return (NotificationRuleResponse)created.Value!;
    }

    [Fact]
    public async Task Create_PersistsRule_AndRedactsRouteSecret()
    {
        await using var db = TestDbFactory.Create();
        var h = Build(db);

        var created = await Create(h.Ctrl, Req("rule",
            routes: [new NotificationRouteDto(null, "GenericWebhook", "https://hook", "hmac-secret", 0)]));

        created.Name.Should().Be("rule");
        created.Routes.Should().HaveCount(1);
        created.Routes[0].Secret.Should().Be(NotificationRuleStore.UnchangedSecret, "the cipher is never returned — only the keep-sentinel");
        created.Routes[0].Target.Should().Be("https://hook");
        // The real secret is decryptable from storage.
        (await h.Store.GetRouteSecretAsync(created.Routes[0].Id!.Value, CancellationToken.None)).Should().Be("hmac-secret");
    }

    [Fact]
    public async Task GetAll_ReturnsCreatedRules()
    {
        await using var db = TestDbFactory.Create();
        var h = Build(db);
        await Create(h.Ctrl, Req("a"));
        await Create(h.Ctrl, Req("b"));

        var ok = (await h.Ctrl.GetAll(CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>().Subject;
        ((List<NotificationRuleResponse>)ok.Value!).Select(r => r.Name).Should().Contain(["a", "b"]);
    }

    [Fact]
    public async Task GetAll_ExcludesSystemPolicies_AndGetIsKindScoped()
    {
        await using var db = TestDbFactory.Create();
        var h = Build(db);
        await Create(h.Ctrl, Req("custom-rule"));
        // A system policy persisted directly on the shared table must be invisible to the custom endpoint (A11).
        var system = await h.Store.CreateAsync(new NotificationRule
        {
            Name = "system-policy", EventTypes = "SystemAlert", Kind = NotificationRuleKind.System, SystemSourceId = "backlog",
        }, "admin", CancellationToken.None);

        var ok = (await h.Ctrl.GetAll(CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>().Subject;
        ((List<NotificationRuleResponse>)ok.Value!).Select(r => r.Name).Should().Contain("custom-rule").And.NotContain("system-policy");
        (await h.Ctrl.Get(system.Id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>("the custom endpoint is kind-scoped");
    }

    [Fact]
    public void GetCatalog_ReturnsSupportedEventsFieldsAndChannels()
    {
        using var db = TestDbFactory.Create();
        var h = Build(db);

        var catalog = (AlertingCatalogResponse)((OkObjectResult)h.Ctrl.GetCatalog().Result!).Value!;

        // Infra/signal types moved to system policies (ADR 0008) — the custom catalog only offers the
        // execution-family types now.
        catalog.EventTypes.Select(e => e.Name).Should().BeEquivalentTo(
            ["ExecutionFailed", "ExecutionSucceeded", "ExecutionCancelled", "ExecutionRunningLong", "ExecutionQueuedLong", "CredentialFailure"]);
        catalog.EventTypes.Select(e => e.Name).Should().NotContain(["BacklogHigh", "MachineUnreachable", "ScheduleMissed"]);
        catalog.EventFields.Select(f => f.Name).Should().Contain(["eventType", "durationMs", "signalValue"]);
        catalog.Channels.Should().BeEquivalentTo(["Email", "GenericWebhook"]);
        catalog.DedupTemplateFields.Should().Contain("workflowId");
    }

    [Fact]
    public async Task Create_RoundTripsDedupTemplateAndRouteCondition()
    {
        await using var db = TestDbFactory.Create();
        var h = Build(db);
        const string routeCondition = """
        {"type":"comparison","op":"==","left":{"kind":"variable","source":"event","name":"severity"},"right":{"kind":"literal","value":"Warning"}}
        """;
        var req = new CreateNotificationRuleRequest(
            "rule", null, true, ["ExecutionFailed"], null, "Global", 0, 1, 0,
            [new NotificationRouteDto(null, "Email", "ops@x", null, 0, routeCondition)],
            null,
            "{{eventType}}:{{workflowId}}");

        var created = await Create(h.Ctrl, req);

        created.DedupKeyTemplate.Should().Be("{{eventType}}:{{workflowId}}");
        created.Routes.Single().ConditionExpressionJson.Should().Be(routeCondition);
    }

    [Fact]
    public async Task Update_WithSentinelSecret_KeepsStoredSecret()
    {
        await using var db = TestDbFactory.Create();
        var h = Build(db);
        var created = await Create(h.Ctrl, Req("rule",
            routes: [new NotificationRouteDto(null, "GenericWebhook", "https://hook", "secret-1", 0)]));
        var routeId = created.Routes[0].Id!.Value;

        var update = new UpdateNotificationRuleRequest("rule-renamed", null, true, ["ExecutionFailed"], null, "Global", 0, 1, 0,
            [new NotificationRouteDto(routeId, "GenericWebhook", "https://hook-2", NotificationRuleStore.UnchangedSecret, 0)], null);
        (await h.Ctrl.Update(created.Id, update, CancellationToken.None)).Should().BeOfType<NoContentResult>();

        var reread = (NotificationRuleResponse)((OkObjectResult)(await h.Ctrl.Get(created.Id, CancellationToken.None)).Result!).Value!;
        reread.Name.Should().Be("rule-renamed");
        reread.Routes[0].Target.Should().Be("https://hook-2");
        (await h.Store.GetRouteSecretAsync(routeId, CancellationToken.None)).Should().Be("secret-1", "sentinel keeps the stored secret across an edit");
    }

    [Fact]
    public async Task Delete_RemovesRule()
    {
        await using var db = TestDbFactory.Create();
        var h = Build(db);
        var created = await Create(h.Ctrl, Req("rule"));

        (await h.Ctrl.Delete(created.Id, CancellationToken.None)).Should().BeOfType<NoContentResult>();
        (await h.Ctrl.Get(created.Id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Disable_ThenEnable_TogglesEnabledState()
    {
        await using var db = TestDbFactory.Create();
        var h = Build(db);
        var created = await Create(h.Ctrl, Req("rule")); // Req defaults isEnabled: true

        (await h.Ctrl.Disable(created.Id, CancellationToken.None)).Should().BeOfType<NoContentResult>();
        (await h.Store.GetByKindAsync(created.Id, NotificationRuleKind.Custom, CancellationToken.None))!.IsEnabled.Should().BeFalse();

        (await h.Ctrl.Enable(created.Id, CancellationToken.None)).Should().BeOfType<NoContentResult>();
        (await h.Store.GetByKindAsync(created.Id, NotificationRuleKind.Custom, CancellationToken.None))!.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task EnableDisable_AreKindScoped_NotFoundForSystemPolicy()
    {
        await using var db = TestDbFactory.Create();
        var h = Build(db);
        // A system policy on the shared table must be untouchable via the custom enable/disable surface (A11).
        var system = await h.Store.CreateAsync(new NotificationRule
        {
            Name = "system-policy", EventTypes = "SystemAlert", Kind = NotificationRuleKind.System, SystemSourceId = "backlog",
        }, "admin", CancellationToken.None);

        (await h.Ctrl.Enable(system.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await h.Ctrl.Disable(system.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await h.Ctrl.Enable(Guid.NewGuid(), CancellationToken.None)).Should().BeOfType<NotFoundResult>("unknown id");
    }

    [Fact]
    public async Task Create_InvalidEventType_Returns400()
    {
        await using var db = TestDbFactory.Create();
        var h = Build(db);
        (await h.Ctrl.Create(Req("rule", events: "NoSuchEvent"), CancellationToken.None)).Result
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_NoRoutes_Returns400()
    {
        await using var db = TestDbFactory.Create();
        var h = Build(db);
        (await h.Ctrl.Create(Req("rule", routes: []), CancellationToken.None)).Result
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData("BacklogHigh")]
    [InlineData("MachineUnreachable")]
    [InlineData("ServiceStale")]
    [InlineData("PendingHigh")]
    [InlineData("CancelRateHigh")]
    [InlineData("CredentialExpiring")]
    [InlineData("ScheduleMissed")]
    [InlineData("WorkflowNoRecentSuccess")]
    public async Task Create_InfraSignalType_Rejected_MovedToSystemPolicies(string eventType)
    {
        // The legacy gauge path was retired (ADR 0008) — infra/signal alerts are now system policies, so the
        // custom-rule surface no longer accepts these event types (whatever scope).
        await using var db = TestDbFactory.Create();
        var h = Build(db);
        (await h.Ctrl.Create(Req($"{eventType}-rule", events: eventType), CancellationToken.None)).Result
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData("ExecutionRunningLong")]
    [InlineData("ExecutionQueuedLong")]
    [InlineData("CredentialFailure")]
    public async Task Create_WorkflowScopedEvent_AllowsWorkflowScope(string eventType)
    {
        // ExecutionRunningLong is execution-scoped (NOT a gauge), so a Workflows-scoped rule is valid —
        // this is the regression guard against accidentally classifying it as gauge (Global-only).
        await using var db = TestDbFactory.Create();
        var h = Build(db);
        var created = await Create(h.Ctrl, Req($"{eventType}-rule", events: eventType, scope: "Workflows",
            targets: [new NotificationRuleTargetDto("Workflow", Guid.NewGuid())]));
        created.EventTypes.Should().Contain(eventType);
        created.ScopeKind.Should().Be("Workflows");
    }

    [Fact]
    public async Task Create_CooldownOverCap_Returns400()
    {
        await using var db = TestDbFactory.Create();
        var h = Build(db);
        // 200000 minutes (~139d) exceeds the 30-day throttle cap → must be rejected so the retention
        // sweep can never wipe an still-active cooldown row.
        (await h.Ctrl.Create(Req("rule", cooldown: 200000), CancellationToken.None)).Result
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_ChannelWithoutRegisteredSink_Returns400()
    {
        await using var db = TestDbFactory.Create();
        var h = Build(db); // only Email + GenericWebhook sinks registered
        var req = Req("rule", routes: [new NotificationRouteDto(null, "Teams", "https://teams.example", null, 0)]);
        (await h.Ctrl.Create(req, CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task TestFire_SendsThroughRoutes_AndRecordsTestAttempts()
    {
        await using var db = TestDbFactory.Create();
        var h = Build(db);
        var created = await Create(h.Ctrl, Req("rule",
            routes: [new NotificationRouteDto(null, "Email", "ops@x", null, 0)]));

        var resp = (TestFireResponse)((OkObjectResult)(await h.Ctrl.TestFire(created.Id, CancellationToken.None)).Result!).Value!;

        resp.AllSucceeded.Should().BeTrue();
        resp.Results.Should().ContainSingle().Which.Channel.Should().Be("Email");
        h.Email.Sends.Should().ContainSingle().Which.target.Should().Be("ops@x");
        (await db.NotificationDeliveryAttempts.CountAsync(a => a.IsTest)).Should().Be(1);
    }

    [Theory]
    [InlineData("Prod", true)]
    [InlineData("Other", false)]
    public void PreviewFilter_EvaluatesEventFieldSource(string workflowName, bool expected)
    {
        using var db = TestDbFactory.Create();
        var h = Build(db);
        const string filter = """
        {"type":"comparison","op":"==","left":{"kind":"variable","source":"event","name":"workflowName"},"right":{"kind":"literal","value":"Prod"}}
        """;

        var resp = (PreviewFilterResponse)((OkObjectResult)h.Ctrl.PreviewFilter(
            new PreviewFilterRequest(filter, new Dictionary<string, string> { ["workflowName"] = workflowName })).Result!).Value!;

        resp.Matches.Should().Be(expected);
    }

    [Fact]
    public void PreviewRule_EvaluatesRuleAndRouteConditions()
    {
        using var db = TestDbFactory.Create();
        var h = Build(db);
        const string ruleFilter = """
        {"type":"comparison","op":"==","left":{"kind":"variable","source":"event","name":"status"},"right":{"kind":"literal","value":"Failed"}}
        """;
        const string routeCondition = """
        {"type":"comparison","op":"==","left":{"kind":"variable","source":"event","name":"severity"},"right":{"kind":"literal","value":"Warning"}}
        """;

        var request = new PreviewRuleRequest(
            ["ExecutionFailed"],
            ruleFilter,
            "Global",
            [new NotificationRouteDto(null, "Email", "ops@x", null, 0, routeCondition)],
            [],
            "{{eventType}}",
            new Dictionary<string, string> { ["eventType"] = "ExecutionFailed", ["status"] = "Failed", ["severity"] = "Warning" });

        var resp = (PreviewRuleResponse)((OkObjectResult)h.Ctrl.PreviewRule(request).Result!).Value!;

        resp.MatchesRule.Should().BeTrue();
        resp.DedupKey.Should().Be("ExecutionFailed");
        resp.Routes.Should().ContainSingle().Which.Matches.Should().BeTrue();
    }

    private static async Task SeedAttempt(NodePilotDbContext db, Guid ruleId, Guid routeId, string eventKey,
        NotificationDeliveryStatus status, DateTime createdAt, string? error = null)
    {
        db.NotificationDeliveryAttempts.Add(new NotificationDeliveryAttempt
        {
            Id = Guid.NewGuid(), NotificationRuleId = ruleId, NotificationRouteId = routeId,
            EventKey = eventKey, DedupKey = "k", Status = status, Attempt = 1, CreatedAt = createdAt,
            SentAt = createdAt, Error = error,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetDeliveries_ReturnsLedger_NewestFirst_WithRuleNameAndChannel()
    {
        await using var db = TestDbFactory.Create();
        var h = Build(db);
        var rule = await Create(h.Ctrl, Req("ledger-rule", routes: [new NotificationRouteDto(null, "Email", "ops@x", null, 0)]));
        var routeId = rule.Routes[0].Id!.Value;
        await SeedAttempt(db, rule.Id, routeId, "e1", NotificationDeliveryStatus.Sent, DateTime.UtcNow.AddMinutes(-1));
        await SeedAttempt(db, rule.Id, routeId, "e2", NotificationDeliveryStatus.Failed, DateTime.UtcNow, "smtp down");

        var ok = (await h.Ctrl.GetDeliveries(null, null, 0)).Result.Should().BeOfType<OkObjectResult>().Subject;
        var list = (List<NotificationDeliveryDto>)ok.Value!;

        list.Should().HaveCount(2);
        list[0].EventKey.Should().Be("e2", "newest first");
        list.Should().OnlyContain(d => d.RuleName == "ledger-rule");
        list.Should().Contain(d => d.Channel == "Email" && d.Target == "ops@x");
    }

    [Fact]
    public async Task GetDeliveries_FilterByStatus_ReturnsOnlyMatching()
    {
        await using var db = TestDbFactory.Create();
        var h = Build(db);
        var rule = await Create(h.Ctrl, Req("ledger-rule", routes: [new NotificationRouteDto(null, "Email", "ops@x", null, 0)]));
        var routeId = rule.Routes[0].Id!.Value;
        await SeedAttempt(db, rule.Id, routeId, "e1", NotificationDeliveryStatus.Sent, DateTime.UtcNow.AddMinutes(-1));
        await SeedAttempt(db, rule.Id, routeId, "e2", NotificationDeliveryStatus.Failed, DateTime.UtcNow, "smtp down");

        var ok = (await h.Ctrl.GetDeliveries(null, "Failed", 0)).Result.Should().BeOfType<OkObjectResult>().Subject;
        ((List<NotificationDeliveryDto>)ok.Value!).Should().ContainSingle().Which.Status.Should().Be("Failed");
    }
}
