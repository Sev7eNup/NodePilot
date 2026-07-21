using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

/// <summary>
/// Coverage for the not-found / validation / preview / no-sink branches of
/// <see cref="AlertingController"/> that the happy-path <see cref="AlertingControllerTests"/>
/// does not reach.
/// </summary>
public class AlertingControllerExtraTests
{
    private static byte[] Key()
    {
        var k = new byte[32];
        for (var i = 0; i < k.Length; i++) k[i] = (byte)(i + 11);
        return k;
    }

    private sealed class RecordingSink(NotificationChannel channel) : INotificationSink
    {
        public NotificationChannel Channel { get; } = channel;
        public Task<NotificationSendResult> SendAsync(NotificationContext ctx, string target, string? secret, CancellationToken ct)
            => Task.FromResult(NotificationSendResult.Ok);
    }

    private static AlertingController NewController(NodePilotDbContext db, NotificationRuleStore store, bool withSinks)
    {
        INotificationSink[] sinks = withSinks
            ? [new RecordingSink(NotificationChannel.Email), new RecordingSink(NotificationChannel.GenericWebhook)]
            : [];
        var ctrl = new AlertingController(store, db, NoopAuditWriter.Instance, sinks);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "Admin"), new Claim(ClaimTypes.Name, "admin")], "test"));
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
        return ctrl;
    }

    private static CreateNotificationRuleRequest Req(string name, string? filter = null,
        IReadOnlyList<NotificationRouteDto>? routes = null)
        => new(name, null, true, ["ExecutionFailed"], filter, "Global", 0, 1, 0,
            routes ?? [new NotificationRouteDto(null, "Email", "ops@x", null, 0)], null);

    private static async Task<NotificationRuleResponse> Create(AlertingController ctrl, CreateNotificationRuleRequest req)
    {
        var created = (await ctrl.Create(req, CancellationToken.None)).Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        return (NotificationRuleResponse)created.Value!;
    }

    [Fact]
    public async Task Get_UnknownId_Returns404()
    {
        await using var db = TestDbFactory.Create();
        var store = new NotificationRuleStore(db, new AesGcmSecretProtector(Key()));
        var ctrl = NewController(db, store, withSinks: true);
        (await ctrl.Get(Guid.NewGuid(), CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Update_UnknownId_Returns404()
    {
        await using var db = TestDbFactory.Create();
        var store = new NotificationRuleStore(db, new AesGcmSecretProtector(Key()));
        var ctrl = NewController(db, store, withSinks: true);
        var update = new UpdateNotificationRuleRequest("ghost", null, true, ["ExecutionFailed"], null, "Global", 0, 1, 0,
            [new NotificationRouteDto(null, "Email", "ops@x", null, 0)], null);
        (await ctrl.Update(Guid.NewGuid(), update, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Update_InvalidPayload_Returns400()
    {
        await using var db = TestDbFactory.Create();
        var store = new NotificationRuleStore(db, new AesGcmSecretProtector(Key()));
        var ctrl = NewController(db, store, withSinks: true);
        var update = new UpdateNotificationRuleRequest("x", null, true, ["NoSuchEvent"], null, "Global", 0, 1, 0,
            [new NotificationRouteDto(null, "Email", "ops@x", null, 0)], null);
        (await ctrl.Update(Guid.NewGuid(), update, CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Delete_UnknownId_Returns404()
    {
        await using var db = TestDbFactory.Create();
        var store = new NotificationRuleStore(db, new AesGcmSecretProtector(Key()));
        var ctrl = NewController(db, store, withSinks: true);
        (await ctrl.Delete(Guid.NewGuid(), CancellationToken.None)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task TestFire_UnknownId_Returns404()
    {
        await using var db = TestDbFactory.Create();
        var store = new NotificationRuleStore(db, new AesGcmSecretProtector(Key()));
        var ctrl = NewController(db, store, withSinks: true);
        (await ctrl.TestFire(Guid.NewGuid(), CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetDeliveries_InvalidStatus_Returns400()
    {
        await using var db = TestDbFactory.Create();
        var store = new NotificationRuleStore(db, new AesGcmSecretProtector(Key()));
        var ctrl = NewController(db, store, withSinks: true);
        (await ctrl.GetDeliveries(null, "Bogus", 0)).Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetDeliveries_NoAttempts_ReturnsEmptyList()
    {
        await using var db = TestDbFactory.Create();
        var store = new NotificationRuleStore(db, new AesGcmSecretProtector(Key()));
        var ctrl = NewController(db, store, withSinks: true);
        var ok = (await ctrl.GetDeliveries(null, null, 0)).Result.Should().BeOfType<OkObjectResult>().Subject;
        ((List<NotificationDeliveryDto>)ok.Value!).Should().BeEmpty();
    }

    [Fact]
    public async Task TestFire_RouteWithNoRegisteredSink_ReportsFailure()
    {
        await using var db = TestDbFactory.Create();
        var store = new NotificationRuleStore(db, new AesGcmSecretProtector(Key()));
        // Create with sinks present (passes validation), then test-fire with a sink-less controller.
        var created = await Create(NewController(db, store, withSinks: true), Req("rule"));
        var sinkless = NewController(db, store, withSinks: false);

        var resp = (TestFireResponse)((OkObjectResult)(await sinkless.TestFire(created.Id, CancellationToken.None)).Result!).Value!;

        resp.AllSucceeded.Should().BeFalse();
        resp.Results.Should().ContainSingle().Which.Success.Should().BeFalse();
        (await db.NotificationDeliveryAttempts.CountAsync(a => a.IsTest && a.Status == NotificationDeliveryStatus.Failed))
            .Should().Be(1);
    }

    [Fact]
    public void PreviewFilter_EmptyFilter_MatchesEverything()
    {
        using var db = TestDbFactory.Create();
        var store = new NotificationRuleStore(db, new AesGcmSecretProtector(Key()));
        var ctrl = NewController(db, store, withSinks: true);
        var resp = (PreviewFilterResponse)((OkObjectResult)ctrl.PreviewFilter(new PreviewFilterRequest(null, null)).Result!).Value!;
        resp.Matches.Should().BeTrue();
    }

    [Fact]
    public void PreviewFilter_InvalidJson_ReturnsNoMatchWithReason()
    {
        using var db = TestDbFactory.Create();
        var store = new NotificationRuleStore(db, new AesGcmSecretProtector(Key()));
        var ctrl = NewController(db, store, withSinks: true);
        var resp = (PreviewFilterResponse)((OkObjectResult)ctrl.PreviewFilter(
            new PreviewFilterRequest("{ this is not json", null)).Result!).Value!;
        resp.Matches.Should().BeFalse();
        resp.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void PreviewRule_InvalidEventType_Returns400()
    {
        using var db = TestDbFactory.Create();
        var store = new NotificationRuleStore(db, new AesGcmSecretProtector(Key()));
        var ctrl = NewController(db, store, withSinks: true);
        var req = new PreviewRuleRequest(["NoSuchEvent"], null, "Global",
            [new NotificationRouteDto(null, "Email", "ops@x", null, 0)], [], null, new Dictionary<string, string>());
        ctrl.PreviewRule(req).Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void PreviewRule_RuleFilterMismatch_ReportsReason()
    {
        using var db = TestDbFactory.Create();
        var store = new NotificationRuleStore(db, new AesGcmSecretProtector(Key()));
        var ctrl = NewController(db, store, withSinks: true);
        const string filter = """
        {"type":"comparison","op":"==","left":{"kind":"variable","source":"event","name":"workflowName"},"right":{"kind":"literal","value":"Prod"}}
        """;
        var req = new PreviewRuleRequest(["ExecutionFailed"], filter, "Global",
            [new NotificationRouteDto(null, "Email", "ops@x", null, 0)], [], null,
            new Dictionary<string, string> { ["eventType"] = "ExecutionFailed", ["workflowName"] = "Other" });

        var resp = (PreviewRuleResponse)((OkObjectResult)ctrl.PreviewRule(req).Result!).Value!;
        resp.MatchesRule.Should().BeFalse();
        resp.Reasons.Should().Contain(r => r.Contains("filter", StringComparison.OrdinalIgnoreCase));
        resp.DedupKey.Should().BeNull();
    }

    [Fact]
    public void PreviewRule_RuleMatchesButNoRouteCondition_ReportsNoRouteMatched()
    {
        using var db = TestDbFactory.Create();
        var store = new NotificationRuleStore(db, new AesGcmSecretProtector(Key()));
        var ctrl = NewController(db, store, withSinks: true);
        // Route condition requires severity == Critical; sample fields leave it unset → route won't match.
        const string routeCondition = """
        {"type":"comparison","op":"==","left":{"kind":"variable","source":"event","name":"severity"},"right":{"kind":"literal","value":"Critical"}}
        """;
        var req = new PreviewRuleRequest(["ExecutionFailed"], null, "Global",
            [new NotificationRouteDto(null, "Email", "ops@x", null, 0, routeCondition)], [], null,
            new Dictionary<string, string> { ["eventType"] = "ExecutionFailed", ["severity"] = "Info" });

        var resp = (PreviewRuleResponse)((OkObjectResult)ctrl.PreviewRule(req).Result!).Value!;
        resp.MatchesRule.Should().BeTrue();
        resp.Routes.Should().ContainSingle().Which.Matches.Should().BeFalse();
        resp.Reasons.Should().Contain(r => r.Contains("route", StringComparison.OrdinalIgnoreCase));
    }
}
