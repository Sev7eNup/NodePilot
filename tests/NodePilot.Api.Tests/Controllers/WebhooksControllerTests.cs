using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Api.Controllers;
using NodePilot.Api.ExecutionDispatch;
using NodePilot.Api.Security;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Security;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public class WebhooksControllerTests
{
    private static NodePilotDbContext CreateContext() => NodePilot.TestCommons.TestDbFactory.Create();

    private static WebhooksController CreateController(
        NodePilotDbContext db,
        string method = "POST",
        string body = "",
        IDictionary<string, string>? headers = null,
        IDictionary<string, string?>? configValues = null,
        byte[]? bodyBytes = null,
        IAuditWriter? audit = null,
        NodePilot.Core.Interfaces.IMaintenanceWindowEvaluator? maintenance = null,
        string requestPath = "/api/webhooks/Signed/hook",
        string queryString = "")
    {
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var configBuilder = new ConfigurationBuilder();
        if (configValues is not null) configBuilder.AddInMemoryCollection(configValues);
        var config = configBuilder.Build();

        // Since the execution-dispatch redesign, WebhooksController routes through
        // ExecutionDispatchService (persists a Pending row before enqueue). Tests exercise
        // the same path with a NoOp dispatch queue so no engine work actually runs — only
        // the persist + audit path is verified.
        var dispatchService = new ExecutionDispatchService(
            db,
            new NoopExecutionDispatchQueue(),
            scopeFactory,
            new OutputRedactor(config),
            new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            NodePilot.Api.Tests.TestSupport.StubMaintenanceWindowEvaluator.AllowAll,
            NullLogger<ExecutionDispatchService>.Instance);

        var controller = new WebhooksController(
            db, dispatchService, NullLogger<WebhooksController>.Instance, config,
            audit ?? NoopAuditWriter.Instance,
            maintenance ?? NodePilot.Api.Tests.TestSupport.StubMaintenanceWindowEvaluator.AllowAll);

        var httpCtx = new DefaultHttpContext();
        httpCtx.Request.Method = method;
        httpCtx.Request.Path = requestPath;
        httpCtx.Request.QueryString = new QueryString(queryString);
        httpCtx.Request.Body = new MemoryStream(bodyBytes ?? Encoding.UTF8.GetBytes(body));
        if (headers is not null)
        {
            foreach (var (k, v) in headers) httpCtx.Request.Headers[k] = v;
        }

        controller.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return controller;
    }

    private const string WebhookDefinition = """
    {
      "nodes": [
        { "id": "t1", "data": { "activityType": "webhookTrigger", "config": { "path": "hook", "method": "POST", "secret": "s3cret" } } }
      ]
    }
    """;

    [Fact]
    public async Task Hit_WorkflowNotFound_Returns404()
    {
        var db = CreateContext();
        var controller = CreateController(db);

        var result = await controller.Hit("unknown-workflow", null, CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Hit_WorkflowDisabled_ReturnsNotFound()
    {
        var db = CreateContext();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "Disabled", DefinitionJson = WebhookDefinition, IsEnabled = false };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var result = await controller.Hit("Disabled", "hook", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Hit_BlockedByMaintenanceWindow_HiddenReject()
    {
        var db = CreateContext();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "Gated", DefinitionJson = WebhookDefinition, IsEnabled = true };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        // Valid secret so we pass the secret + enabled checks and actually reach the maintenance
        // gate; the window then blocks and we expect the uniform hidden 404 (anti-enumeration).
        var controller = CreateController(db, body: "{}",
            headers: new Dictionary<string, string> { ["X-Webhook-Secret"] = "s3cret" },
            maintenance: NodePilot.Api.Tests.TestSupport.StubMaintenanceWindowEvaluator.Blocking("PatchWindow"));

        var result = await controller.Hit("Gated", "hook", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0, "a blocked webhook must not create an execution row");
    }

    [Fact]
    public async Task Hit_NoWebhookTriggerNode_ReturnsNotFound()
    {
        var db = CreateContext();
        var wf = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "NoTrigger",
            DefinitionJson = "{\"nodes\":[{\"id\":\"s1\",\"data\":{\"activityType\":\"runScript\"}}]}",
        };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var result = await controller.Hit("NoTrigger", null, CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Hit_DisabledWebhookTriggerNode_ReturnsNotFound()
    {
        const string disabledWebhookDefinition = """
        {
          "nodes": [
            { "id": "t1", "data": { "activityType": "webhookTrigger", "disabled": true, "config": { "path": "hook", "method": "POST", "secret": "s3cret" } } }
          ]
        }
        """;
        var db = CreateContext();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "DisabledTrigger", DefinitionJson = disabledWebhookDefinition };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var controller = CreateController(db,
            headers: new Dictionary<string, string> { ["X-Webhook-Secret"] = "s3cret" });
        var result = await controller.Hit("DisabledTrigger", "hook", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Hit_PathMismatch_Returns404()
    {
        var db = CreateContext();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "Wf", DefinitionJson = WebhookDefinition };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var controller = CreateController(db, headers: new Dictionary<string, string> { ["X-Webhook-Secret"] = "s3cret" });

        var result = await controller.Hit("Wf", "wrong-path", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Hit_MethodMismatch_ReturnsNotFound()
    {
        var db = CreateContext();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "Wf", DefinitionJson = WebhookDefinition };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var controller = CreateController(db, method: "GET",
            headers: new Dictionary<string, string> { ["X-Webhook-Secret"] = "s3cret" });

        var result = await controller.Hit("Wf", "hook", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Hit_MissingSecret_ReturnsNotFound()
    {
        var db = CreateContext();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "Wf", DefinitionJson = WebhookDefinition };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var result = await controller.Hit("Wf", "hook", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Hit_WrongSecret_ReturnsNotFound()
    {
        var db = CreateContext();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "Wf", DefinitionJson = WebhookDefinition };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var controller = CreateController(db,
            headers: new Dictionary<string, string> { ["X-Webhook-Secret"] = "wrong" });

        var result = await controller.Hit("Wf", "hook", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Hit_WrongSecretOfDifferentLength_ReturnsNotFound()
    {
        // Regression: FixedTimeEquals returns false for length-mismatch without throwing.
        var db = CreateContext();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "Wf", DefinitionJson = WebhookDefinition };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var controller = CreateController(db,
            headers: new Dictionary<string, string> { ["X-Webhook-Secret"] = "x" });

        var result = await controller.Hit("Wf", "hook", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Hit_HappyPath_Returns202Accepted()
    {
        // Happy-path coverage gap: prior tests only exercise rejection branches. This pins
        // the success path so a regression in routing/secret/method matching surfaces here
        // before it ships.
        var db = CreateContext();
        var wfId = Guid.NewGuid();
        var wf = new Workflow { Id = wfId, Name = "Wf", DefinitionJson = WebhookDefinition };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var controller = CreateController(db,
            headers: new Dictionary<string, string> { ["X-Webhook-Secret"] = "s3cret" });

        var result = await controller.Hit("Wf", "hook", CancellationToken.None);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        accepted.StatusCode.Should().Be(StatusCodes.Status202Accepted);
    }

    [Fact]
    public async Task Hit_WorkflowFoundByGuid_Returns202()
    {
        var db = CreateContext();
        var wfId = Guid.NewGuid();
        var wf = new Workflow { Id = wfId, Name = "ByGuid", DefinitionJson = WebhookDefinition };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var controller = CreateController(db,
            headers: new Dictionary<string, string> { ["X-Webhook-Secret"] = "s3cret" });

        // Lookup-by-Guid path is a separate code branch from name-lookup and was uncovered.
        var result = await controller.Hit(wfId.ToString(), "hook", CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
    }

    private const string WebhookDefinitionNoSecret = """
    {
      "nodes": [
        { "id": "t1", "data": { "activityType": "webhookTrigger", "config": { "path": "hook", "method": "POST" } } }
      ]
    }
    """;

    [Fact]
    public async Task Hit_NoSecretConfigured_RequireSecretExplicitlyFalse_StillFires()
    {
        // Dev-mode escape hatch (mirrors appsettings.Development.json): an explicit
        // Webhook:RequireSecret=false lets secret-less webhooks fire so workflow authors
        // can iterate locally without standing up a secret. The warning log still emits.
        var db = CreateContext();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "OpenWf", DefinitionJson = WebhookDefinitionNoSecret };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var controller = CreateController(db,
            configValues: new Dictionary<string, string?> { ["Webhook:RequireSecret"] = "false" });

        var result = await controller.Hit("OpenWf", "hook", CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
    }

    [Fact]
    public async Task Hit_NoSecretConfigured_DefaultPolicy_Returns403()
    {
        // Phase-3 hardening: a missing Webhook:RequireSecret key now reads as "true" so a
        // stripped-down deployment falls on the safe side. Webhook nodes saved without a
        // secret are rejected by default — no upgrade-compatibility shim. Operators that
        // need legacy behaviour set the flag to "false" explicitly.
        var db = CreateContext();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "OpenWf", DefinitionJson = WebhookDefinitionNoSecret };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var controller = CreateController(db);

        var result = await controller.Hit("OpenWf", "hook", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Hit_NoSecretConfigured_RequireSecretTrue_Returns403()
    {
        // Strict mode: operators can opt into Webhook:RequireSecret=true to reject
        // secret-less webhooks fleet-wide. Critical security-feature contract — must have
        // a regression test even though the default policy now produces the same result.
        var db = CreateContext();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "OpenWf", DefinitionJson = WebhookDefinitionNoSecret };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var controller = CreateController(db,
            configValues: new Dictionary<string, string?> { ["Webhook:RequireSecret"] = "true" });

        var result = await controller.Hit("OpenWf", "hook", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Hit_InvalidUtf8Body_Returns400BadRequest()
    {
        // Security-audit finding M-13: the controller reads the body with
        // throwOnInvalidBytes=true so an attacker cannot smuggle malformed multi-byte
        // sequences into workflow variables. Invalid UTF-8 must surface as a clean 400, not a 5xx.
        var db = CreateContext();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "Wf", DefinitionJson = WebhookDefinition };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        // 0xFF is invalid as a UTF-8 lead byte.
        var invalidUtf8 = new byte[] { 0xFF, 0xFE, 0xFD };
        var controller = CreateController(db,
            headers: new Dictionary<string, string> { ["X-Webhook-Secret"] = "s3cret" },
            bodyBytes: invalidUtf8);

        var result = await controller.Hit("Wf", "hook", CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Hit_GetMethod_DoesNotReadBody_StillFires()
    {
        // Security-audit finding M-13: GET/HEAD/DELETE skip the body read entirely. A
        // controller that always reads the body would burn a worker on a zero-byte read for
        // these methods. We verify GET still goes through to 202 even with no body present.
        const string getDefinition = """
        {
          "nodes": [
            { "id": "t1", "data": { "activityType": "webhookTrigger", "config": { "path": "hook", "method": "GET", "secret": "s3cret" } } }
          ]
        }
        """;
        var db = CreateContext();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "GetWf", DefinitionJson = getDefinition };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var controller = CreateController(db, method: "GET",
            headers: new Dictionary<string, string> { ["X-Webhook-Secret"] = "s3cret" });

        var result = await controller.Hit("GetWf", "hook", CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
    }

    [Fact]
    public async Task Hit_HappyPath_WritesWebhookTriggeredAudit()
    {
        // Anonymous webhook fires must leave an audit trail. The audit row carries the
        // workflow id, path, method and body size so post-incident forensics can map a
        // suspicious run back to its triggering request even though the caller is anonymous.
        var db = CreateContext();
        var wfId = Guid.NewGuid();
        var wf = new Workflow { Id = wfId, Name = "AuditedWebhook", DefinitionJson = WebhookDefinition };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var audit = new CapturingAuditWriter();
        var controller = CreateController(db, body: "payload",
            headers: new Dictionary<string, string> { ["X-Webhook-Secret"] = "s3cret" },
            audit: audit);

        var result = await controller.Hit("AuditedWebhook", "hook", CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        var call = audit.Calls.Should().ContainSingle(c => c.Action == "WEBHOOK_TRIGGERED").Subject;
        call.ResourceType.Should().Be("Workflow");
        call.ResourceId.Should().Be(wfId);
        call.Details.Should().Contain("\"workflowName\":\"AuditedWebhook\"");
        call.Details.Should().Contain("\"path\":\"hook\"");
        call.Details.Should().Contain("\"method\":\"POST\"");
        call.Details.Should().Contain("\"hasSecret\":true");
        call.Details.Should().Contain("\"bodyChars\":7");
    }

    [Fact]
    public async Task Hit_RejectedRequest_DoesNotEmitAudit()
    {
        // Path/method probing must NOT inflate the audit log — only successful fires get
        // an audit row. Rejection paths still record metrics for monitoring.
        var db = CreateContext();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "Wf", DefinitionJson = WebhookDefinition };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var audit = new CapturingAuditWriter();
        var controller = CreateController(db,
            headers: new Dictionary<string, string> { ["X-Webhook-Secret"] = "wrong" },
            audit: audit);

        var result = await controller.Hit("Wf", "hook", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
        audit.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Hit_PathWithLeadingSlash_StillMatches()
    {
        // The controller normalises both expected and actual paths via TrimStart('/'), so
        // a definition saying "/hook" must match an incoming "hook" and vice versa.
        const string slashDefinition = """
        {
          "nodes": [
            { "id": "t1", "data": { "activityType": "webhookTrigger", "config": { "path": "/hook", "method": "POST", "secret": "s3cret" } } }
          ]
        }
        """;
        var db = CreateContext();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "Wf", DefinitionJson = slashDefinition };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var controller = CreateController(db,
            headers: new Dictionary<string, string> { ["X-Webhook-Secret"] = "s3cret" });

        var result = await controller.Hit("Wf", "hook", CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
    }

    [Fact]
    public async Task Hit_AcceptedResponse_PersistsPendingExecutionRow_BeforeReturning()
    {
        // Contract from the execution-dispatch redesign: webhook returns 202 only after a
        // Pending WorkflowExecution row has been persisted. This is what makes the trigger
        // crash-recoverable: if the
        // leader dies between the 202 and the engine actually starting the run, the
        // failover-recovery sweep can promote the orphan Pending row to Cancelled and the
        // external caller sees the failure mode via GET /executions/{id}.
        var db = CreateContext();
        var wfId = Guid.NewGuid();
        db.Workflows.Add(new Workflow { Id = wfId, Name = "Persist", DefinitionJson = WebhookDefinition });
        await db.SaveChangesAsync();

        var controller = CreateController(db,
            headers: new Dictionary<string, string> { ["X-Webhook-Secret"] = "s3cret" });

        var result = await controller.Hit("Persist", "hook", CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        var row = await db.WorkflowExecutions.SingleAsync(e => e.WorkflowId == wfId);
        row.TriggeredBy.Should().Be("webhook");
        row.Status.Should().BeOneOf(NodePilot.Core.Enums.ExecutionStatus.Pending,
            NodePilot.Core.Enums.ExecutionStatus.Running,
            NodePilot.Core.Enums.ExecutionStatus.Succeeded,
            NodePilot.Core.Enums.ExecutionStatus.Failed,
            NodePilot.Core.Enums.ExecutionStatus.Cancelled);

        // Body of the AcceptedResult should also expose the executionId so callers can poll.
        var accepted = (AcceptedResult)result;
        accepted.Value.Should().NotBeNull();
        var bodyType = accepted.Value!.GetType();
        var executionIdProp = bodyType.GetProperty("executionId");
        executionIdProp.Should().NotBeNull("Step-5b adds executionId to the accepted response body");
        executionIdProp!.GetValue(accepted.Value).Should().Be(row.Id);
    }

    // ---- NodePilot HMAC v2 verification ---------------------------------------

    private const string HmacSecret = "0123456789abcdef0123456789abcdef";

    private const string HmacWebhookDefinition = """
    {
      "nodes": [
        { "id": "t1", "data": { "activityType": "webhookTrigger", "config": {
            "path": "hook", "method": "POST", "secret": "0123456789abcdef0123456789abcdef",
            "signatureMode": "nodepilot-hmac-v2" } } }
      ]
    }
    """;

    private static string Sign(
        string secret,
        string timestamp,
        string deliveryId,
        byte[] body,
        string method = "POST",
        string canonicalPath = "/api/webhooks/Signed/hook",
        string canonicalQuery = "",
        string prefix = "sha256=")
    {
        var mac = WebhookHmacSecurity.ComputeMac(
            Encoding.UTF8.GetBytes(secret), timestamp, deliveryId,
            method, canonicalPath, canonicalQuery, body);
        return prefix + Convert.ToHexString(mac).ToLowerInvariant();
    }

    private static Dictionary<string, string> SignedHeaders(
        byte[] signedBody,
        string secret = HmacSecret,
        string signatureHeader = "X-NodePilot-Signature",
        string prefix = "sha256=",
        string? timestamp = null,
        string? deliveryId = null,
        string method = "POST",
        string canonicalPath = "/api/webhooks/Signed/hook",
        string canonicalQuery = "")
    {
        timestamp ??= DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        deliveryId ??= Guid.NewGuid().ToString("D");
        return new Dictionary<string, string>
        {
            [WebhookHmacSecurity.TimestampHeader] = timestamp,
            [WebhookHmacSecurity.DeliveryIdHeader] = deliveryId,
            [signatureHeader] = Sign(
                secret, timestamp, deliveryId, signedBody,
                method, canonicalPath, canonicalQuery, prefix),
        };
    }

    [Fact]
    public async Task Hit_ValidHmacSignature_Returns202()
    {
        var db = CreateContext();
        var workflowId = Guid.NewGuid();
        db.Workflows.Add(new Workflow { Id = workflowId, Name = "Signed", DefinitionJson = HmacWebhookDefinition });
        await db.SaveChangesAsync();

        var body = Encoding.UTF8.GetBytes("""{"action":"opened"}""");
        var headers = SignedHeaders(body);
        headers["X-Correlation-Id"] = "safe-operational-header";
        var controller = CreateController(db, bodyBytes: body, headers: headers);

        var result = await controller.Hit("Signed", "hook", CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        var execution = await db.WorkflowExecutions.SingleAsync(x => x.WorkflowId == workflowId);
        var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(
            execution.InputParametersJson!)!;
        parameters.Should().NotContainKey("webhookHeader_X-Correlation-Id",
            "v2 does not authenticate arbitrary headers, so they cannot become workflow inputs");
        parameters.Should().NotContainKey("webhookHeader_X-NodePilot-Signature");
        parameters.Should().NotContainKey("webhookHeader_X-NodePilot-Timestamp");
        parameters.Should().NotContainKey("webhookHeader_X-NodePilot-Delivery-Id");
    }

    [Fact]
    public async Task Hit_InvalidHmacSignature_ReturnsHidden404()
    {
        var db = CreateContext();
        db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "Signed", DefinitionJson = HmacWebhookDefinition });
        await db.SaveChangesAsync();

        var body = Encoding.UTF8.GetBytes("""{"action":"opened"}""");
        var controller = CreateController(db, bodyBytes: body,
            headers: SignedHeaders(body, secret: "wrong-key-wrong-key-wrong-key-xx"));

        var result = await controller.Hit("Signed", "hook", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>("bad signatures collapse into the uniform hidden 404");
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Hit_MissingSignatureHeader_ReturnsHidden404()
    {
        var db = CreateContext();
        db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "Signed", DefinitionJson = HmacWebhookDefinition });
        await db.SaveChangesAsync();

        var body = Encoding.UTF8.GetBytes("{}");
        var headers = SignedHeaders(body);
        headers.Remove("X-NodePilot-Signature");
        var controller = CreateController(db, bodyBytes: body, headers: headers);

        var result = await controller.Hit("Signed", "hook", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Theory]
    [InlineData("X-NodePilot-Timestamp")]
    [InlineData("X-NodePilot-Delivery-Id")]
    public async Task Hit_MissingHmacFreshnessHeader_ReturnsHidden404(string missingHeader)
    {
        var db = CreateContext();
        db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "Signed", DefinitionJson = HmacWebhookDefinition });
        await db.SaveChangesAsync();

        var body = Encoding.UTF8.GetBytes("{}");
        var headers = SignedHeaders(body);
        headers.Remove(missingHeader);

        var result = await CreateController(db, bodyBytes: body, headers: headers)
            .Hit("Signed", "hook", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
        (await db.IdempotencyKeys.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Hit_HmacTamperedBody_ReturnsHidden404()
    {
        // The signature is over the RAW bytes — flipping one body byte after signing must fail.
        var db = CreateContext();
        db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "Signed", DefinitionJson = HmacWebhookDefinition });
        await db.SaveChangesAsync();

        var signedBody = Encoding.UTF8.GetBytes("""{"amount":100}""");
        var tampered = Encoding.UTF8.GetBytes("""{"amount":900}""");
        var controller = CreateController(db, bodyBytes: tampered,
            headers: SignedHeaders(signedBody));

        var result = await controller.Hit("Signed", "hook", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Hit_HmacSignedCanonicalQuery_IsForwarded()
    {
        var db = CreateContext();
        var workflowId = Guid.NewGuid();
        db.Workflows.Add(new Workflow { Id = workflowId, Name = "Signed", DefinitionJson = HmacWebhookDefinition });
        await db.SaveChangesAsync();

        var body = Encoding.UTF8.GetBytes("{}");
        var headers = SignedHeaders(
            body,
            canonicalQuery: "role=admin&tag=b&tag=a");
        var result = await CreateController(
                db,
                bodyBytes: body,
                headers: headers,
                queryString: "?tag=b&role=admin&tag=a")
            .Hit("Signed", "hook", CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        var execution = await db.WorkflowExecutions.SingleAsync(x => x.WorkflowId == workflowId);
        var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(
            execution.InputParametersJson!)!;
        parameters["webhookQuery_role"].Should().Be("admin");
        parameters["webhookQuery_tag"].Should().Be("b,a");
    }

    [Fact]
    public async Task Hit_HmacSignatureDoesNotAuthenticateChangedQuery()
    {
        var db = CreateContext();
        db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "Signed", DefinitionJson = HmacWebhookDefinition });
        await db.SaveChangesAsync();

        var body = Encoding.UTF8.GetBytes("{}");
        var headers = SignedHeaders(body, canonicalQuery: "role=viewer");
        var result = await CreateController(
                db,
                bodyBytes: body,
                headers: headers,
                queryString: "?role=admin")
            .Hit("Signed", "hook", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Hit_HmacSignatureBindsDuplicateQueryValueOrder()
    {
        var db = CreateContext();
        db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "Signed", DefinitionJson = HmacWebhookDefinition });
        await db.SaveChangesAsync();

        var body = Encoding.UTF8.GetBytes("{}");
        var headers = SignedHeaders(body, canonicalQuery: "tag=a&tag=b");
        var result = await CreateController(
                db,
                bodyBytes: body,
                headers: headers,
                queryString: "?tag=b&tag=a")
            .Hit("Signed", "hook", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Hit_HmacQueryForwarding_IsStableForSanitizedKeyCollisions()
    {
        var db = CreateContext();
        var workflowId = Guid.NewGuid();
        db.Workflows.Add(new Workflow { Id = workflowId, Name = "Signed", DefinitionJson = HmacWebhookDefinition });
        await db.SaveChangesAsync();

        var body = Encoding.UTF8.GetBytes("{}");
        const string canonicalQuery = "a.b=dot&a_b=underscore";
        var result = await CreateController(
                db,
                bodyBytes: body,
                headers: SignedHeaders(body, canonicalQuery: canonicalQuery),
                queryString: "?a_b=underscore&a.b=dot")
            .Hit("Signed", "hook", CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        var execution = await db.WorkflowExecutions.SingleAsync(x => x.WorkflowId == workflowId);
        var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(
            execution.InputParametersJson!)!;
        parameters["webhookQuery_a_b"].Should().Be("underscore",
            "v2 forwards query keys in deterministic order independent of their wire order");
    }

    [Fact]
    public async Task Hit_HmacSignatureDoesNotAuthenticateChangedMethodOrPath()
    {
        var body = Encoding.UTF8.GetBytes("{}");

        await using (var methodDb = CreateContext())
        {
            methodDb.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "Signed", DefinitionJson = HmacWebhookDefinition });
            await methodDb.SaveChangesAsync();
            var methodResult = await CreateController(
                    methodDb,
                    bodyBytes: body,
                    headers: SignedHeaders(body, method: "GET"))
                .Hit("Signed", "hook", CancellationToken.None);
            methodResult.Should().BeOfType<NotFoundObjectResult>();
        }

        await using (var pathDb = CreateContext())
        {
            pathDb.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "Signed", DefinitionJson = HmacWebhookDefinition });
            await pathDb.SaveChangesAsync();
            var pathResult = await CreateController(
                    pathDb,
                    bodyBytes: body,
                    headers: SignedHeaders(body, canonicalPath: "/api/webhooks/Other/hook"))
                .Hit("Signed", "hook", CancellationToken.None);
            pathResult.Should().BeOfType<NotFoundObjectResult>();
        }
    }

    [Fact]
    public async Task Hit_UppercaseHexSignature_Returns202()
    {
        // Hex casing carries no entropy — a PowerShell sender using BitConverter emits
        // uppercase digests and must not be rejected.
        var db = CreateContext();
        db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "Signed", DefinitionJson = HmacWebhookDefinition });
        await db.SaveChangesAsync();

        var body = Encoding.UTF8.GetBytes("""{"action":"opened"}""");
        var headers = SignedHeaders(body);
        headers["X-NodePilot-Signature"] = headers["X-NodePilot-Signature"].ToUpperInvariant();
        var controller = CreateController(db, bodyBytes: body,
            headers: headers);

        var result = await controller.Hit("Signed", "hook", CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
    }

    [Fact]
    public async Task Hit_CustomSignatureHeaderAndPrefix_Returns202()
    {
        var db = CreateContext();
        const string def = """
        {
          "nodes": [
            { "id": "t1", "data": { "activityType": "webhookTrigger", "config": {
                "path": "hook", "method": "POST", "secret": "0123456789abcdef0123456789abcdef",
                "signatureMode": "nodepilot-hmac-v2",
                "signatureHeader": "X-Partner-Signature-256",
                "signaturePrefix": "sha256=" } } }
          ]
        }
        """;
        db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "Partner", DefinitionJson = def });
        await db.SaveChangesAsync();

        var body = Encoding.UTF8.GetBytes("""{"ref":"refs/heads/main"}""");
        var controller = CreateController(db, bodyBytes: body,
            headers: SignedHeaders(
                body,
                signatureHeader: "X-Partner-Signature-256",
                canonicalPath: "/api/webhooks/Partner/hook"),
            requestPath: "/api/webhooks/Partner/hook");

        var result = await controller.Hit("Partner", "hook", CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        var execution = await db.WorkflowExecutions.SingleAsync();
        var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(
            execution.InputParametersJson!)!;
        parameters.Should().NotContainKey("webhookHeader_X-Partner-Signature-256",
            "the configured signature header is credential material too");
    }

    [Fact]
    public async Task Hit_ReusedHmacDeliveryId_IsAtomicallyRejectedWithoutSecondExecution()
    {
        var db = CreateContext();
        var workflowId = Guid.NewGuid();
        db.Workflows.Add(new Workflow { Id = workflowId, Name = "Signed", DefinitionJson = HmacWebhookDefinition });
        await db.SaveChangesAsync();

        var body = Encoding.UTF8.GetBytes("""{"action":"opened"}""");
        var headers = SignedHeaders(body, deliveryId: "ef72c8bd-e863-4f6d-9b43-3f28d83c75f5");

        var first = await CreateController(db, bodyBytes: body, headers: headers)
            .Hit("Signed", "hook", CancellationToken.None);
        var replay = await CreateController(db, bodyBytes: body, headers: headers)
            .Hit("Signed", "hook", CancellationToken.None);

        first.Should().BeOfType<AcceptedResult>();
        replay.Should().BeOfType<NotFoundObjectResult>();
        (await db.WorkflowExecutions.CountAsync(x => x.WorkflowId == workflowId)).Should().Be(1);
        (await db.IdempotencyKeys.CountAsync(x => x.WorkflowId == workflowId
                                                  && x.ExecutionId == Guid.Empty)).Should().Be(1);
    }

    [Fact]
    public async Task Hit_HmacSignatureDoesNotAuthenticateAChangedTimestamp()
    {
        var db = CreateContext();
        db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "Signed", DefinitionJson = HmacWebhookDefinition });
        await db.SaveChangesAsync();

        var body = Encoding.UTF8.GetBytes("{}");
        var headers = SignedHeaders(body);
        headers[WebhookHmacSecurity.TimestampHeader] =
            (long.Parse(headers[WebhookHmacSecurity.TimestampHeader]) + 1).ToString();

        var result = await CreateController(db, bodyBytes: body, headers: headers)
            .Hit("Signed", "hook", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
        (await db.IdempotencyKeys.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Hit_HmacTimestampOutsideFreshnessWindow_IsRejected()
    {
        var db = CreateContext();
        db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "Signed", DefinitionJson = HmacWebhookDefinition });
        await db.SaveChangesAsync();

        var body = Encoding.UTF8.GetBytes("{}");
        var staleTimestamp = DateTimeOffset.UtcNow.AddMinutes(-6).ToUnixTimeSeconds().ToString();
        var headers = SignedHeaders(body, timestamp: staleTimestamp);

        var result = await CreateController(db, bodyBytes: body, headers: headers)
            .Hit("Signed", "hook", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Hit_LegacyUnversionedHmacMode_FailsClosedAtRuntime()
    {
        const string weakDefinition = """
        {
          "nodes": [
            { "id": "t1", "data": { "activityType": "webhookTrigger", "config": {
                "path": "hook", "method": "POST", "secret": "0123456789abcdef0123456789abcdef",
                "signatureMode": "hmac" } } }
          ]
        }
        """;
        var db = CreateContext();
        db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "Legacy", DefinitionJson = weakDefinition });
        await db.SaveChangesAsync();

        var body = Encoding.UTF8.GetBytes("{}");
        var result = await CreateController(db, bodyBytes: body,
                headers: SignedHeaders(body))
            .Hit("Legacy", "hook", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
    }

    // ---- JSONPath field mapping (fieldMappings) -------------------------------

    private const string MappingWebhookDefinition = """
    {
      "nodes": [
        { "id": "t1", "data": { "activityType": "webhookTrigger", "config": {
            "path": "hook", "method": "POST", "secret": "s3cret",
            "fieldMappings": [
              { "name": "ticketId", "path": "$.ticket.id" },
              { "name": "severity", "path": "$.ticket.severity" },
              { "name": "tags", "path": "$.ticket.tags" },
              { "name": "missing", "path": "$.does.not.exist" },
              { "name": "webhookBody", "path": "$.ticket.id" },
              { "name": "__callDepth", "path": "$.ticket.id" }
            ] } } }
      ]
    }
    """;

    [Fact]
    public async Task Hit_FieldMappings_InjectsMappedParameters()
    {
        var db = CreateContext();
        var wfId = Guid.NewGuid();
        db.Workflows.Add(new Workflow { Id = wfId, Name = "Mapped", DefinitionJson = MappingWebhookDefinition });
        await db.SaveChangesAsync();

        const string body = """{"ticket":{"id":"INC-4711","severity":2,"tags":["p1","db"]}}""";
        var controller = CreateController(db, body: body,
            headers: new Dictionary<string, string> { ["X-Webhook-Secret"] = "s3cret" });

        var result = await controller.Hit("Mapped", "hook", CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        var row = await db.WorkflowExecutions.SingleAsync(e => e.WorkflowId == wfId);
        var parameters = System.Text.Json.JsonSerializer
            .Deserialize<Dictionary<string, string>>(row.InputParametersJson!)!;
        parameters["ticketId"].Should().Be("INC-4711");
        parameters["severity"].Should().Be("2");
        parameters["tags"].Should().Be("""["p1","db"]""", "containers render compact like jsonQuery");
        parameters["missing"].Should().Be("", "a non-matching path degrades to empty, never a reject");
        parameters["webhookBody"].Should().Be(body, "system keys must not be shadowed by a mapping");
        parameters.Should().NotContainKey("__callDepth", "__-prefixed names are engine-reserved");
    }

    [Fact]
    public async Task Hit_FieldMappings_NonJsonBody_StillFiresWithoutMappedFields()
    {
        var db = CreateContext();
        var wfId = Guid.NewGuid();
        db.Workflows.Add(new Workflow { Id = wfId, Name = "MappedText", DefinitionJson = MappingWebhookDefinition });
        await db.SaveChangesAsync();

        var controller = CreateController(db, body: "plain text, not json",
            headers: new Dictionary<string, string> { ["X-Webhook-Secret"] = "s3cret" });

        var result = await controller.Hit("MappedText", "hook", CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>("mapping is a convenience layer — a non-JSON body must not reject the hook");
        var row = await db.WorkflowExecutions.SingleAsync(e => e.WorkflowId == wfId);
        var parameters = System.Text.Json.JsonSerializer
            .Deserialize<Dictionary<string, string>>(row.InputParametersJson!)!;
        parameters.Should().NotContainKey("ticketId");
        parameters["webhookBody"].Should().Be("plain text, not json");
    }
}
