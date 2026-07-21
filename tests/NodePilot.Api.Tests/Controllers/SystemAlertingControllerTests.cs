using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Security;
using NodePilot.Scheduler.SystemAlerts;
using NodePilot.Scheduler.SystemAlerts.Sources;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public class SystemAlertingControllerTests
{
    private static byte[] Key()
    {
        var k = new byte[32];
        for (var i = 0; i < k.Length; i++) k[i] = (byte)(i + 5);
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

    private static (SystemAlertingController ctrl, NotificationRuleStore store, RecordingSink sink) Build(NodePilotDbContext db)
    {
        var catalog = new SystemAlertCatalog([new BacklogSource(), new MachineUnreachableSource(), new ExecutionResultSource()]);
        var store = new NotificationRuleStore(db, new AesGcmSecretProtector(Key()));
        var sink = new RecordingSink(NotificationChannel.Email);
        var ctrl = new SystemAlertingController(catalog, db, store, NoopAuditWriter.Instance, [sink], NullLogger<SystemAlertingController>.Instance);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "Admin"), new Claim(ClaimTypes.Name, "admin")], "test"));
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
        return (ctrl, store, sink);
    }

    private static SaveSystemAlertPolicyRequest Req(
        string name = "backlog-warn", string sourceId = "backlog", bool enabled = true, string scope = "Global",
        string? condition = null, int sustain = 0, string? severityOverride = null,
        IReadOnlyDictionary<string, object?>? sourceParams = null,
        IReadOnlyList<NotificationRouteDto>? routes = null, IReadOnlyList<NotificationRuleTargetDto>? targets = null)
        => new(name, null, enabled, sourceId, null, sourceParams,
            condition ?? SystemAlertConditionJson("depth", ">", "500"), sustain, severityOverride, scope, targets,
            routes ?? [new NotificationRouteDto(null, "Email", "ops@x", null, 0)], 0, 1, 0);

    private static string SystemAlertConditionJson(string field, string op, string literal)
        => SystemAlertConditions.Compare(field, op, literal);

    private static async Task<SystemAlertPolicyResponse> Create(SystemAlertingController ctrl, SaveSystemAlertPolicyRequest req)
    {
        var created = (await ctrl.Create(req, CancellationToken.None)).Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        return (SystemAlertPolicyResponse)created.Value!;
    }

    // ---- catalog (carried from the foundation phase) ----

    [Fact]
    public async Task GetCatalog_ReturnsAllSources_WithParametersAndAvailability()
    {
        await using var db = TestDbFactory.Create();
        var (ctrl, _, _) = Build(db);

        var catalog = (await ctrl.GetCatalog(CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>()
            .Subject.Value.Should().BeOfType<SystemAlertCatalogResponse>().Subject;

        catalog.Sources.Select(s => s.SourceId).Should().Equal("backlog", "execution-result", "machine-unreachable");
        catalog.Sources.Single(s => s.SourceId == "execution-result").Parameters.Single().Name.Should().Be("lookbackSeconds");
        catalog.Sources.Single(s => s.SourceId == "machine-unreachable").Available.Should().BeFalse("no machines checked");
    }

    [Fact]
    public void MutationEndpoints_AreAdminOnly()
    {
        foreach (var name in new[] { nameof(SystemAlertingController.Create), nameof(SystemAlertingController.Update),
            nameof(SystemAlertingController.Delete), nameof(SystemAlertingController.Enable),
            nameof(SystemAlertingController.Disable), nameof(SystemAlertingController.TestFire) })
        {
            var attr = typeof(SystemAlertingController).GetMethod(name)!.GetCustomAttribute<AuthorizeAttribute>();
            attr.Should().NotBeNull($"{name} must be Admin-only");
            attr!.Roles.Should().Be("Admin");
        }
    }

    // ---- create / validation ----

    [Fact]
    public async Task Create_PersistsSystemPolicy_WithKindSystem()
    {
        await using var db = TestDbFactory.Create();
        var (ctrl, store, _) = Build(db);

        var resp = await Create(ctrl, Req());

        resp.SourceId.Should().Be("backlog");
        resp.IsEnabled.Should().BeTrue();
        resp.ActivatedAt.Should().NotBeNull("enabling stamps the activation watermark");
        var stored = await store.GetByKindAsync(resp.Id, NotificationRuleKind.System, CancellationToken.None);
        stored!.EventTypes.Should().Be("SystemAlert");
    }

    [Fact]
    public async Task Create_UnknownSource_Returns400()
    {
        await using var db = TestDbFactory.Create();
        var (ctrl, _, _) = Build(db);

        var result = await ctrl.Create(Req(sourceId: "does-not-exist"), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_EnabledWithoutRoute_Returns400()
    {
        await using var db = TestDbFactory.Create();
        var (ctrl, _, _) = Build(db);

        var result = await ctrl.Create(Req(routes: []), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_ConditionOnUnknownField_ReturnsValidationProblem()
    {
        await using var db = TestDbFactory.Create();
        var (ctrl, _, _) = Build(db);

        var result = await ctrl.Create(Req(condition: SystemAlertConditionJson("nonsense", ">", "1")), CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.Value.Should().BeOfType<ValidationProblemDetails>()
            .Which.Errors.Keys.Should().Contain(k => k.Contains("name"));
    }

    [Fact]
    public async Task Create_GlobalOnlySource_WithWorkflowScope_Returns400()
    {
        await using var db = TestDbFactory.Create();
        var (ctrl, _, _) = Build(db);

        var result = await ctrl.Create(Req(scope: "Workflows",
            targets: [new NotificationRuleTargetDto("Workflow", Guid.NewGuid())]), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ---- enable / disable / update / delete ----

    [Fact]
    public async Task Disable_ThenEnable_TogglesAndRestampsActivation()
    {
        await using var db = TestDbFactory.Create();
        var (ctrl, store, _) = Build(db);
        var created = await Create(ctrl, Req());

        (await ctrl.Disable(created.Id, CancellationToken.None)).Should().BeOfType<NoContentResult>();
        (await store.GetByKindAsync(created.Id, NotificationRuleKind.System, CancellationToken.None))!.IsEnabled.Should().BeFalse();

        (await ctrl.Enable(created.Id, CancellationToken.None)).Should().BeOfType<NoContentResult>();
        (await store.GetByKindAsync(created.Id, NotificationRuleKind.System, CancellationToken.None))!.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_RemovesPolicy()
    {
        await using var db = TestDbFactory.Create();
        var (ctrl, store, _) = Build(db);
        var created = await Create(ctrl, Req());

        (await ctrl.Delete(created.Id, CancellationToken.None)).Should().BeOfType<NoContentResult>();

        (await store.GetByKindAsync(created.Id, NotificationRuleKind.System, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Update_ChangingCondition_ResetsPolicyState()
    {
        await using var db = TestDbFactory.Create();
        var (ctrl, _, _) = Build(db);
        var created = await Create(ctrl, Req());
        db.SystemAlertPolicyStates.Add(new SystemAlertPolicyState
        { Id = Guid.NewGuid(), NotificationRuleId = created.Id, SourceId = "backlog", InstanceKey = "backlog" });
        await db.SaveChangesAsync();

        var result = await ctrl.Update(created.Id, Req(condition: SystemAlertConditionJson("depth", ">", "999")), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        db.SystemAlertPolicyStates.Any(s => s.NotificationRuleId == created.Id).Should().BeFalse("changing the condition resets transient state");
    }

    // ---- preview + test-fire ----

    [Fact]
    public async Task Preview_ReportsMatchesAgainstCurrentObservations()
    {
        await using var db = TestDbFactory.Create();
        var (ctrl, _, _) = Build(db);
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "wf", DefinitionJson = "{}" };
        db.Workflows.Add(wf);
        db.WorkflowExecutions.Add(new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = wf.Id, Status = ExecutionStatus.Running });
        await db.SaveChangesAsync();

        var request = new SystemAlertPreviewRequest("backlog", null, SystemAlertConditionJson("depth", ">=", "1"));
        var resp = (await ctrl.Preview(request, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>()
            .Subject.Value.Should().BeOfType<SystemAlertPreviewResponse>().Subject;

        resp.Available.Should().BeTrue();
        resp.Matches.Should().ContainSingle().Which.Matched.Should().BeTrue();
    }

    [Fact]
    public async Task TestFire_SendsThroughRoutes_AndRecordsIsTestAttempt()
    {
        await using var db = TestDbFactory.Create();
        var (ctrl, _, sink) = Build(db);
        var created = await Create(ctrl, Req());

        var resp = (await ctrl.TestFire(created.Id, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>()
            .Subject.Value.Should().BeOfType<TestFireResponse>().Subject;

        resp.AllSucceeded.Should().BeTrue();
        sink.Sends.Should().ContainSingle().Which.EventType.Should().Be(NotificationEventType.SystemAlert);
        db.NotificationDeliveryAttempts.Should().ContainSingle().Which.IsTest.Should().BeTrue();
    }
}
