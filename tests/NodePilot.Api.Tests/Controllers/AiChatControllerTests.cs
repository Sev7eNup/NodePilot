using NodePilot.Ai;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodePilot.Api.Ai;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.Security;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public class AiChatControllerTests
{
    private const string SampleWorkflow = """
        { "nodes": [ { "id": "t1", "type": "activity", "position": {"x":0,"y":0},
            "data": { "label": "Start", "activityType": "manualTrigger", "config": {} } } ], "edges": [] }
        """;

    // Mirrors the internal WorkflowAssistantService.DefinitionDelimiter. Kept as a local copy so
    // this Api-side test does not depend on NodePilot.Ai internals (the assistant lives in a
    // separate assembly now; only NodePilot.Ai.Tests holds the InternalsVisibleTo grant).
    private const string DefinitionDelimiter = "===NODEPILOT-DEFINITION===";

    private static (AiChatController controller, CapturingAuditWriter audit, FakeLlmClient llm, MemoryStream body, NodePilotDbContext db)
        Build(bool enabled = true, string role = "Operator", bool aborted = false, bool enableToolCalling = false)
    {
        var options = new StaticOptionsMonitor<LlmOptions>(new LlmOptions
        {
            Enabled = enabled,
            BaseUrl = "http://localhost/v1",
            Model = "test-model",
            MaxTokens = 100,
            TimeoutSeconds = 30,
            EnableToolCalling = enableToolCalling,
        });
        var llm = new FakeLlmClient();
        var db = TestDbFactory.Create();
        // Real reader backed by the test DB: the gating tests observe which tools get advertised
        // (llm.Calls[..].Tools) — the controller decides whether the reader's tools make it into the context.
        var assistant = new WorkflowAssistantService(llm, new PromptCatalog(), new WorkflowChatToolRegistry(), options,
            customStore: null, executionLogs: new ExecutionLogReader(db, new StubAuditDetailsRedactor()));
        var audit = new CapturingAuditWriter();
        var authz = new ResourceAuthorizationService(db);
        var controller = new AiChatController(options, assistant, audit, db, authz, NullLogger<AiChatController>.Instance);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Role, role), new Claim(ClaimTypes.Name, "tester") }, "TestAuth"));
        var body = new MemoryStream();
        var ctx = new DefaultHttpContext { User = principal };
        ctx.Response.Body = body;
        if (aborted) ctx.RequestAborted = new CancellationToken(canceled: true);
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return (controller, audit, llm, body, db);
    }

    private static (AiChatController controller, CapturingAuditWriter audit, FakeLlmClient llm, MemoryStream body)
        NewController(bool enabled = true, string role = "Operator", bool aborted = false)
    {
        var (controller, audit, llm, body, _) = Build(enabled, role, aborted);
        return (controller, audit, llm, body);
    }

    private static WorkflowChatRequest Req(string question = "Was macht der Workflow?",
        string? workflowJson = null, IReadOnlyList<AiChatTurnDto>? history = null,
        Guid? workflowId = null, bool noWorkflowId = false)
        => new(question, workflowJson ?? SampleWorkflow, noWorkflowId ? null : workflowId ?? Guid.NewGuid(), "hash-1",
               history ?? Array.Empty<AiChatTurnDto>());

    private static string[] Modify(string prose, string definitionJson) =>
        new[] { prose, "\n" + DefinitionDelimiter + "\n", definitionJson };

    private static List<(string ev, string data)> ParseSse(MemoryStream body)
    {
        var text = Encoding.UTF8.GetString(body.ToArray());
        var events = new List<(string, string)>();
        foreach (var frame in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string ev = "message", data = "";
            foreach (var line in frame.Split('\n'))
            {
                if (line.StartsWith("event:", StringComparison.Ordinal)) ev = line[6..].Trim();
                else if (line.StartsWith("data:", StringComparison.Ordinal)) data = line[5..].Trim();
            }
            events.Add((ev, data));
        }
        return events;
    }

    // ---- Pre-stream checks (normal HTTP) --------------------------------------------

    [Fact]
    public async Task Chat_WhenDisabled_Returns503()
    {
        var (controller, _, _, _) = NewController(enabled: false);
        var result = await controller.Chat(Req(), CancellationToken.None);
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Chat_EmptyQuestion_Returns400()
    {
        var (controller, _, _, _) = NewController();
        (await controller.Chat(Req(question: "   "), CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Chat_MalformedWorkflowJson_Returns400()
    {
        var (controller, _, _, _) = NewController();
        var result = await controller.Chat(Req(workflowJson: "{ not json"), CancellationToken.None);
        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.GetType().GetProperty("code")!.GetValue(bad.Value).Should().Be("WORKFLOW_JSON_INVALID");
    }

    [Fact]
    public async Task Chat_HistoryTooLong_Returns400()
    {
        var (controller, _, _, _) = NewController();
        var history = Enumerable.Range(0, 25)
            .Select(i => new AiChatTurnDto(i % 2 == 0 ? "user" : "assistant", $"turn {i}")).ToArray();
        var result = await controller.Chat(Req(history: history), CancellationToken.None);
        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.GetType().GetProperty("code")!.GetValue(bad.Value).Should().Be("HISTORY_TOO_LONG");
    }

    [Fact]
    public async Task Chat_PreStreamLlmError_MappedToHttp_NoAudit()
    {
        var (controller, audit, llm, _) = NewController();
        llm.EnqueueStreamException(new LlmException(LlmErrorKind.Timeout, "slow"));

        var result = await controller.Chat(Req(), CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        obj.Value!.GetType().GetProperty("code")!.GetValue(obj.Value).Should().Be("LLM_TIMEOUT");
        audit.Calls.Should().BeEmpty();
    }

    // ---- Streaming (SSE) ------------------------------------------------------------

    [Fact]
    public async Task Chat_Explanation_StreamsDeltasAndDone_FiresAudit()
    {
        var (controller, audit, llm, body) = NewController();
        llm.EnqueueStream("Der Workflow ", "startet manuell.");

        var result = await controller.Chat(Req("Was macht t1?"), CancellationToken.None);

        result.Should().BeOfType<EmptyResult>();
        controller.Response.ContentType.Should().Be("text/event-stream");
        var events = ParseSse(body);
        events.Should().Contain(e => e.ev == "delta");
        events.Should().Contain(e => e.ev == "done");
        events.Should().NotContain(e => e.ev == "proposal");

        // The done event carries token usage (used for the usage footer in the chat UI).
        var doneData = events.Single(e => e.ev == "done").data;
        doneData.Should().Contain("\"promptTokens\":1");
        doneData.Should().Contain("\"completionTokens\":1");

        var auditCall = audit.Calls.Should().ContainSingle(c => c.Action == "AI_WORKFLOW_EXPLAINED").Subject;
        auditCall.Details.Should().Contain("\"modifyProposed\":false");
        auditCall.Details.Should().NotContain("Was macht t1?");
    }

    [Fact]
    public async Task Chat_Operator_ValidModify_EmitsProposalEvent()
    {
        var (controller, audit, llm, body) = NewController(role: "Operator");
        var definition = """
            { "nodes": [
                { "id": "t1", "type": "activity", "data": { "activityType": "manualTrigger", "config": {} } },
                { "id": "l1", "type": "activity", "position": {"x":300,"y":0}, "data": { "activityType": "log", "config": { "message": "hi" } } }
              ],
              "edges": [ { "id": "e1", "source": "t1", "target": "l1", "type": "labeled", "data": {} } ] }
            """;
        llm.EnqueueStream(Modify("Log ergänzt.", definition));

        await controller.Chat(Req("Füge einen Log-Schritt ein."), CancellationToken.None);

        var events = ParseSse(body);
        events.Should().Contain(e => e.ev == "building"); // "Generating change..." signal sent before the proposal
        events.Should().Contain(e => e.ev == "proposal");
        audit.Calls.Single().Details.Should().Contain("\"modifyProposed\":true");
    }

    [Fact]
    public async Task Chat_Viewer_Modify_NoProposalEvent()
    {
        var (controller, _, llm, body) = NewController(role: "Viewer");
        var definition = """
            { "nodes": [ { "id": "t1", "type": "activity", "data": { "activityType": "manualTrigger", "config": {} } } ], "edges": [] }
            """;
        llm.EnqueueStream(Modify("Versuche zu ändern.", definition));

        await controller.Chat(Req("Lösche alles."), CancellationToken.None);

        ParseSse(body).Should().NotContain(e => e.ev == "proposal");
    }

    [Fact]
    public async Task Chat_Aborted_AuditsCancelled_NotSuccess()
    {
        var (controller, audit, llm, _) = NewController(aborted: true);
        llm.EnqueueStream("partial");

        var result = await controller.Chat(Req(), new CancellationToken(canceled: true));

        result.Should().BeOfType<EmptyResult>();
        // On cancellation there is no success audit entry, but an audit entry with cancelled=true is still written.
        var call = audit.Calls.Should().ContainSingle(c => c.Action == "AI_WORKFLOW_EXPLAINED").Subject;
        call.Details.Should().Contain("\"cancelled\":true");
    }

    // ---- Execution-log tools: folder-RBAC gate --------------------------------------
    // Observed via the tool-advertising on FakeLlmClient (Calls[..].Tools) — the workflow ID is
    // client-controlled, so the controller's gate decides whether the execution-log tools get offered.

    [Fact]
    public async Task Chat_AdminWithSavedWorkflow_AdvertisesExecutionLogTools()
    {
        var (controller, _, llm, _, db) = Build(role: "Admin", enableToolCalling: true);
        var wf = Guid.NewGuid();
        SeedWorkflow(db, wf);
        llm.EnqueueStream("ok");

        await controller.Chat(Req(workflowId: wf), CancellationToken.None);

        var tools = llm.Calls.Single().Tools!.Select(t => t.Name).ToHashSet();
        tools.Should().Contain("list_recent_executions");
        tools.Should().Contain("get_execution_steps");
        tools.Should().Contain("get_failure_context");
    }

    [Fact]
    public async Task Chat_OperatorWithoutFolderAccess_DoesNotAdvertiseExecutionLogTools()
    {
        // Operator without a folder grant: the chat still runs normally (no 404/403), but the
        // execution-log tools are withheld — this keeps the workflow's existence masked from them.
        var (controller, _, llm, body, db) = Build(role: "Operator", enableToolCalling: true);
        var wf = Guid.NewGuid();
        SeedWorkflow(db, wf);
        llm.EnqueueStream("ok");

        await controller.Chat(Req(workflowId: wf), CancellationToken.None);

        var tools = llm.Calls.Single().Tools!.Select(t => t.Name).ToHashSet();
        tools.Should().Contain("analyze_workflow"); // baseline tools stay available
        tools.Should().NotContain("list_recent_executions");
        ParseSse(body).Should().Contain(e => e.ev == "done"); // the chat itself still succeeds
    }

    [Fact]
    public async Task Chat_UnknownWorkflowId_DoesNotAdvertiseExecutionLogTools()
    {
        var (controller, _, llm, _, _) = Build(role: "Admin", enableToolCalling: true);
        llm.EnqueueStream("ok");

        await controller.Chat(Req(workflowId: Guid.NewGuid()), CancellationToken.None); // not seeded in the DB

        llm.Calls.Single().Tools!.Select(t => t.Name).Should().NotContain("list_recent_executions");
    }

    [Fact]
    public async Task Chat_NullWorkflowId_DoesNotAdvertiseExecutionLogTools()
    {
        var (controller, _, llm, _, _) = Build(role: "Admin", enableToolCalling: true);
        llm.EnqueueStream("ok");

        await controller.Chat(Req(noWorkflowId: true), CancellationToken.None); // unsaved workflow (no ID yet)

        llm.Calls.Single().Tools!.Select(t => t.Name).Should().NotContain("list_recent_executions");
    }

    // ---- Audit visibility (PR3-C) ------------------------------------------------

    private static void SeedWorkflow(NodePilotDbContext db, Guid id)
    {
        db.Workflows.Add(new Workflow { Id = id, Name = "wf" }); // FolderId defaults to RootFolderId
        db.SaveChanges();
    }

    [Fact]
    public async Task ChatApplied_AuditsProposalApplied_WithCounts()
    {
        var (controller, audit, _, _, db) = Build(role: "Admin"); // Admin bypasses folder-RBAC
        var wf = Guid.NewGuid();
        SeedWorkflow(db, wf);

        var result = await controller.ChatApplied(new ChatAppliedRequest(wf, 5, 3), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        var call = audit.Calls.Should().ContainSingle(c => c.Action == "AI_PROPOSAL_APPLIED").Subject;
        call.ResourceId.Should().Be(wf);
        call.Details.Should().Contain("\"nodeCount\":5").And.Contain("\"edgeCount\":3");
    }

    [Fact]
    public async Task ChatApplied_EmptyWorkflowId_Returns400_NoAudit()
    {
        var (controller, audit, _, _, _) = Build();
        var result = await controller.ChatApplied(new ChatAppliedRequest(Guid.Empty, 1, 1), CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
        audit.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ChatApplied_UnknownWorkflow_Returns404_NoAudit()
    {
        var (controller, audit, _, _, _) = Build(role: "Admin");
        var result = await controller.ChatApplied(new ChatAppliedRequest(Guid.NewGuid(), 1, 1), CancellationToken.None);
        result.Should().BeOfType<NotFoundResult>();
        audit.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ChatApplied_OperatorWithoutFolderAccess_Returns404_NoAudit()
    {
        // Operator without an explicit folder grant cannot access the workflow → masked as 404,
        // no AI_PROPOSAL_APPLIED written for someone else's workflow.
        var (controller, audit, _, _, db) = Build(role: "Operator");
        var wf = Guid.NewGuid();
        SeedWorkflow(db, wf);

        var result = await controller.ChatApplied(new ChatAppliedRequest(wf, 1, 1), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        audit.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ChatActivity_ReturnsOnlyThisWorkflowsAiEntries_OrderedDesc()
    {
        var (controller, _, _, _, db) = Build(role: "Admin");
        var wf = Guid.NewGuid();
        var other = Guid.NewGuid();
        SeedWorkflow(db, wf);
        db.AuditLog.AddRange(
            new AuditLogEntry { Id = Guid.NewGuid(), Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Action = "AI_WORKFLOW_EXPLAINED", ResourceType = "Workflow", ResourceId = wf },
            new AuditLogEntry { Id = Guid.NewGuid(), Timestamp = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), Action = "AI_PROPOSAL_APPLIED", ResourceType = "Workflow", ResourceId = wf },
            new AuditLogEntry { Id = Guid.NewGuid(), Timestamp = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), Action = "WORKFLOW_UPDATED", ResourceType = "Workflow", ResourceId = wf },     // not an AI action
            new AuditLogEntry { Id = Guid.NewGuid(), Timestamp = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc), Action = "AI_WORKFLOW_EXPLAINED", ResourceType = "Workflow", ResourceId = other }); // different workflow
        await db.SaveChangesAsync();

        var result = await controller.ChatActivity(wf, take: 20, CancellationToken.None);

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var rows = ok!.Value as IReadOnlyList<AiActivityEntryDto>;
        rows.Should().NotBeNull();
        rows!.Select(r => r.Action).Should().Equal("AI_PROPOSAL_APPLIED", "AI_WORKFLOW_EXPLAINED"); // newest first, foreign rows excluded
    }

    [Fact]
    public async Task ChatActivity_OperatorWithoutFolderAccess_Returns404()
    {
        var (controller, _, _, _, db) = Build(role: "Operator");
        var wf = Guid.NewGuid();
        SeedWorkflow(db, wf);

        var result = await controller.ChatActivity(wf, take: 20, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    /// <summary>
    /// Hot-reload: AiChatController reads IOptionsMonitor&lt;LlmOptions&gt;.CurrentValue per request,
    /// so toggling Llm:Enabled in the Settings UI flips the 503 gate live without a restart. Drive
    /// the monitor from disabled→enabled between two Chat calls on the SAME controller/assistant
    /// instance and assert the gate flips.
    /// </summary>
    [Fact]
    public async Task Chat_DisabledGate_FlipsLiveAfterConfigReload()
    {
        var monitor = new MutableOptionsMonitor<LlmOptions>(new LlmOptions
        {
            Enabled = false,
            BaseUrl = "http://localhost/v1",
            Model = "test-model",
            MaxTokens = 100,
            TimeoutSeconds = 30,
        });
        var llm = new FakeLlmClient();
        var db = TestDbFactory.Create();
        var assistant = new WorkflowAssistantService(llm, new PromptCatalog(), new WorkflowChatToolRegistry(), monitor,
            customStore: null, executionLogs: new ExecutionLogReader(db, new StubAuditDetailsRedactor()));
        var audit = new CapturingAuditWriter();
        var authz = new ResourceAuthorizationService(db);
        var controller = new AiChatController(monitor, assistant, audit, db, authz, NullLogger<AiChatController>.Instance);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Role, "Operator"), new Claim(ClaimTypes.Name, "tester") }, "TestAuth"));
        var body = new MemoryStream();
        controller.ControllerContext = new ControllerContext
        { HttpContext = new DefaultHttpContext { User = principal, Response = { Body = body } } };

        // Disabled: gate returns 503 before the LLM is touched.
        (await controller.Chat(Req(), CancellationToken.None))
            .Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);

        // Operator enables LLM in the Settings UI → config reload.
        monitor.Set(new LlmOptions
        {
            Enabled = true,
            BaseUrl = "http://localhost/v1",
            Model = "test-model",
            MaxTokens = 100,
            TimeoutSeconds = 30,
        });
        llm.EnqueueStream("Der Workflow ", "startet manuell.");

        // Same controller + assistant instance: the next request now streams (no 503).
        var result = await controller.Chat(Req("Was macht t1?"), CancellationToken.None);
        result.Should().BeOfType<EmptyResult>();
        controller.Response.ContentType.Should().Be("text/event-stream");
        ParseSse(body).Should().Contain(e => e.ev == "done");
    }

    /// <summary>Minimal immutable IOptionsMonitor test double for the hot-reload swap.</summary>

    /// <summary>
    /// Settable IOptionsMonitor test double for hot-reload tests — mutating CurrentValue fans
    /// out to registered OnChange listeners, mirroring a real reloadOnChange config reload.
    /// </summary>
}
