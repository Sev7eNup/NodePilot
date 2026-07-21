using NodePilot.Ai;
using NodePilot.TestCommons;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace NodePilot.Ai.Tests;

public class WorkflowAssistantServiceTests
{
    private static WorkflowAssistantService NewService(
        FakeLlmClient client, IChatToolRegistry? tools = null, bool enableToolCalling = false, int maxDepth = 4,
        NodePilot.Core.Interfaces.IExecutionLogReader? executionLogs = null)
        => new(client, new PromptCatalog(), tools ?? new WorkflowChatToolRegistry(),
            new StaticOptionsMonitor<LlmOptions>(new LlmOptions { EnableToolCalling = enableToolCalling, ToolCallMaxDepth = maxDepth }),
            customStore: null, executionLogs: executionLogs);

    private const string SampleWorkflow = """
        {
          "nodes": [
            { "id": "t1", "type": "activity", "position": { "x": 0, "y": 0 },
              "data": { "label": "Start", "activityType": "manualTrigger", "config": {} } },
            { "id": "s1", "type": "activity", "position": { "x": 300, "y": 0 },
              "data": { "label": "Call API", "activityType": "restApi",
                        "credentialId": "cred-9",
                        "config": { "url": "https://api.example/x", "apiKey": "SUPER-SECRET" } } }
          ],
          "edges": [
            { "id": "e1", "source": "t1", "target": "s1", "type": "labeled",
              "sourceHandle": "out", "targetHandle": "in", "data": { "condition": "t1.success" } }
          ]
        }
        """;

    private static WorkflowChatRequest Req(string question, string? workflowJson = null,
        IReadOnlyList<AiChatTurnDto>? history = null)
        => new(question, workflowJson ?? SampleWorkflow, Guid.NewGuid(), "base-hash-abc",
               history ?? Array.Empty<AiChatTurnDto>());

    /// <summary>Streams one chat turn and collects the prose reply plus any proposal it produced.</summary>
    private static async Task<(string text, WorkflowChatProposalDto? proposal)> Run(
        WorkflowAssistantService svc, WorkflowChatRequest req, bool allowModify = true,
        bool allowExecutionTools = false)
    {
        using var doc = JsonDocument.Parse(req.WorkflowJson);
        var sb = new StringBuilder();
        WorkflowChatProposalDto? proposal = null;
        await foreach (var e in svc.StreamChatAsync(req, doc.RootElement, allowModify, allowExecutionTools, CancellationToken.None))
        {
            switch (e)
            {
                case ChatStreamEvent.DeltaEvent d: sb.Append(d.Text); break;
                case ChatStreamEvent.ProposalEvent p: proposal = p.Dto; break;
            }
        }
        return (sb.ToString(), proposal);
    }

    /// <summary>Chunks for a modify-stream: prose, then the delimiter, then the definition JSON.</summary>
    private static string[] ModifyStream(string prose, string definitionJson) =>
        new[] { prose, "\n" + WorkflowAssistantService.DefinitionDelimiter + "\n", definitionJson };

    // ---- Explanation path -----------------------------------------------------------

    [Fact]
    public async Task StreamChat_ReplyOnly_NoProposal()
    {
        var fake = new FakeLlmClient().EnqueueStream("Dieser Workflow ", "startet manuell und ruft eine API.");
        var (text, proposal) = await Run(NewService(fake), Req("Was macht der Workflow?"));

        text.Should().Contain("startet manuell");
        proposal.Should().BeNull();
    }

    [Fact]
    public async Task StreamChat_NoDelimiter_AllTextIsReply()
    {
        var fake = new FakeLlmClient().EnqueueStream("Einfach nur Prosa ohne Definition.");
        var (text, proposal) = await Run(NewService(fake), Req("Erkläre."));

        text.Trim().Should().Be("Einfach nur Prosa ohne Definition.");
        proposal.Should().BeNull();
    }

    // ---- Modify path ----------------------------------------------------------------

    [Fact]
    public async Task StreamChat_ValidModify_ReturnsProposal_PreservingFields()
    {
        var definition = """
            { "nodes": [
                { "id": "t1", "type": "activity", "data": { "activityType": "manualTrigger", "config": {} } },
                { "id": "s1", "type": "activity", "data": { "label": "Renamed", "activityType": "restApi",
                    "config": { "url": "https://api.example/x", "apiKey": "***" } } }
              ],
              "edges": [ { "id": "e1", "source": "t1", "target": "s1", "type": "labeled", "data": { "condition": "t1.success" } } ] }
            """;
        var fake = new FakeLlmClient().EnqueueStream(ModifyStream("Schritt 2 umbenannt.", definition));
        var (text, proposal) = await Run(NewService(fake), Req("Benenne Schritt 2 um."));

        text.Should().Contain("umbenannt");
        proposal.Should().NotBeNull();
        proposal!.BaseDefinitionHash.Should().Be("base-hash-abc");
        proposal.NodeCount.Should().Be(2);

        using var doc = JsonDocument.Parse(proposal.DefinitionJson);
        var s1 = doc.RootElement.GetProperty("nodes").EnumerateArray().First(n => n.GetProperty("id").GetString() == "s1");
        s1.GetProperty("data").GetProperty("label").GetString().Should().Be("Renamed");
        s1.GetProperty("position").GetProperty("x").GetInt32().Should().Be(300);             // preserved
        s1.GetProperty("data").GetProperty("credentialId").GetString().Should().Be("cred-9"); // preserved
        s1.GetProperty("data").GetProperty("config").GetProperty("apiKey").GetString().Should().Be("SUPER-SECRET"); // secret restored from the original
        doc.RootElement.GetProperty("edges").EnumerateArray().First().GetProperty("sourceHandle").GetString().Should().Be("out");
    }

    [Fact]
    public async Task StreamChat_ViewerRole_StripsProposal_AddsNote()
    {
        var definition = """
            { "nodes": [ { "id": "t1", "type": "activity", "data": { "activityType": "manualTrigger", "config": {} } } ], "edges": [] }
            """;
        var fake = new FakeLlmClient().EnqueueStream(ModifyStream("Ändere etwas.", definition));
        var (text, proposal) = await Run(NewService(fake), Req("Lösche alles."), allowModify: false);

        proposal.Should().BeNull();
        text.Should().Contain("Operator/Admin");
    }

    [Fact]
    public async Task StreamChat_StructurallyInvalid_SoftRejected()
    {
        var definition = """
            { "nodes": [ { "id": "t1", "type": "activity", "data": { "activityType": "manualTrigger", "config": {} } } ],
              "edges": [ { "id": "e1", "source": "t1", "target": "ghost", "type": "labeled", "data": {} } ] }
            """;
        var fake = new FakeLlmClient().EnqueueStream(ModifyStream("Versuch.", definition));
        var (text, proposal) = await Run(NewService(fake), Req("Mach was kaputt."));

        proposal.Should().BeNull();
        text.Should().Contain("verworfen");
    }

    [Fact]
    public async Task StreamChat_DroppingTheOnlyTrigger_SoftRejected()
    {
        var definition = """
            { "nodes": [ { "id": "s1", "type": "activity", "data": { "label": "x", "activityType": "log", "config": {} } } ], "edges": [] }
            """;
        var fake = new FakeLlmClient().EnqueueStream(ModifyStream("Trigger weg.", definition));
        var (text, proposal) = await Run(NewService(fake), Req("Entferne den Trigger."));

        proposal.Should().BeNull();
        text.Should().Contain("Trigger");
    }

    [Fact]
    public async Task StreamChat_NewNodeWithoutPosition_GetsFallback_AndNote()
    {
        var definition = """
            { "nodes": [
                { "id": "t1", "type": "activity", "data": { "activityType": "manualTrigger", "config": {} } },
                { "id": "log1", "type": "activity", "data": { "label": "Log", "activityType": "log", "config": { "message": "hi" } } }
              ],
              "edges": [ { "id": "e1", "source": "t1", "target": "log1", "type": "labeled", "data": {} } ] }
            """;
        var fake = new FakeLlmClient().EnqueueStream(ModifyStream("Log ergänzt.", definition));
        var (text, proposal) = await Run(NewService(fake), Req("Füge einen Log-Schritt ein."));

        proposal.Should().NotBeNull();
        using var doc = JsonDocument.Parse(proposal!.DefinitionJson);
        var log1 = doc.RootElement.GetProperty("nodes").EnumerateArray().First(n => n.GetProperty("id").GetString() == "log1");
        log1.TryGetProperty("position", out _).Should().BeTrue();
        text.Should().Contain("Tidy");
    }

    [Fact]
    public async Task StreamChat_Modify_EmitsBuildingBeforeProposal()
    {
        var definition = """
            { "nodes": [ { "id": "t1", "type": "activity", "data": { "activityType": "manualTrigger", "config": {} } },
                          { "id": "s1", "type": "activity", "data": { "label": "X", "activityType": "restApi", "config": {} } } ],
              "edges": [ { "id": "e1", "source": "t1", "target": "s1", "type": "labeled", "data": {} } ] }
            """;
        var fake = new FakeLlmClient().EnqueueStream(ModifyStream("Ändere.", definition));
        using var doc = JsonDocument.Parse(SampleWorkflow);

        var events = new List<ChatStreamEvent>();
        await foreach (var e in NewService(fake).StreamChatAsync(Req("Ändere s1."), doc.RootElement, true, false, CancellationToken.None))
            events.Add(e);

        var buildingIdx = events.FindIndex(e => e is ChatStreamEvent.BuildingEvent);
        var proposalIdx = events.FindIndex(e => e is ChatStreamEvent.ProposalEvent);
        buildingIdx.Should().BeGreaterThanOrEqualTo(0);
        proposalIdx.Should().BeGreaterThan(buildingIdx);
    }

    [Fact]
    public async Task StreamChat_ReplyOnly_NoBuildingEvent()
    {
        var fake = new FakeLlmClient().EnqueueStream("Nur eine Erklärung.");
        using var doc = JsonDocument.Parse(SampleWorkflow);

        var events = new List<ChatStreamEvent>();
        await foreach (var e in NewService(fake).StreamChatAsync(Req("Erkläre."), doc.RootElement, true, false, CancellationToken.None))
            events.Add(e);

        events.Should().NotContain(e => e is ChatStreamEvent.BuildingEvent);
    }

    // ---- Prompt / privacy -----------------------------------------------------------

    [Fact]
    public async Task StreamChat_DoesNotSendRealSecretsToLlm_AndDisablesJsonMode()
    {
        var fake = new FakeLlmClient().EnqueueStream("ok");
        await Run(NewService(fake), Req("Erkläre."));

        var call = fake.Calls.Single();
        call.JsonMode.Should().BeFalse(); // the streaming chat doesn't use response_format
        var userTurn = call.Conversation!.Last().Content;
        userTurn.Should().Contain("***");
        userTurn.Should().NotContain("SUPER-SECRET");
    }

    // ---- Empty-canvas design mode -------------------------------------------------

    [Fact]
    public async Task StreamChat_EmptyCanvas_InjectsDesignExample()
    {
        var fake = new FakeLlmClient().EnqueueStream("ok");
        await Run(NewService(fake), Req("Erstelle einen Workflow, der täglich eine Datei prüft.",
            workflowJson: """{ "nodes": [], "edges": [] }"""));

        var sys = fake.Calls.Single().SystemPrompt;
        sys.Should().Contain("Empty canvas — design mode");
        sys.Should().Contain("Reference example workflow"); // includes a rich few-shot example
    }

    [Fact]
    public async Task StreamChat_TriggerOnlyCanvas_IsTreatedAsEmpty()
    {
        var fake = new FakeLlmClient().EnqueueStream("ok");
        await Run(NewService(fake), Req("Bau den Rest.",
            workflowJson: """{ "nodes": [ { "id": "t1", "type": "activity", "data": { "activityType": "scheduleTrigger", "config": {} } } ], "edges": [] }"""));

        fake.Calls.Single().SystemPrompt.Should().Contain("Empty canvas — design mode");
    }

    [Fact]
    public async Task StreamChat_NonEmptyCanvas_NoDesignExample()
    {
        var fake = new FakeLlmClient().EnqueueStream("ok");
        await Run(NewService(fake), Req("Erkläre den Workflow.")); // SampleWorkflow contains a restApi activity

        fake.Calls.Single().SystemPrompt.Should().NotContain("Empty canvas — design mode");
    }

    [Fact]
    public async Task StreamChat_EmptyCanvas_FromScratchDefinition_ReturnsProposal()
    {
        var definition = """
            { "nodes": [
                { "id": "t1", "type": "activity", "position": {"x":0,"y":0}, "data": { "activityType": "scheduleTrigger", "config": {} } },
                { "id": "l1", "type": "activity", "position": {"x":300,"y":0}, "data": { "activityType": "log", "config": { "message": "hi" } } }
              ],
              "edges": [ { "id": "e1", "source": "t1", "target": "l1", "type": "labeled", "data": {} } ] }
            """;
        var fake = new FakeLlmClient().EnqueueStream(ModifyStream("Workflow erstellt.", definition));
        var (_, proposal) = await Run(NewService(fake), Req("Erstelle einen Workflow.",
            workflowJson: """{ "nodes": [], "edges": [] }"""));

        proposal.Should().NotBeNull();
        proposal!.NodeCount.Should().Be(2); // merging onto an empty original workflow works
    }

    [Fact]
    public async Task StreamChat_SystemPromptHasActivityReference_AndHistoryThreaded()
    {
        var history = new[]
        {
            new AiChatTurnDto("user", "Was macht t1?"),
            new AiChatTurnDto("assistant", "Das ist der manuelle Trigger."),
        };
        var fake = new FakeLlmClient().EnqueueStream("ok");
        await Run(NewService(fake), Req("Und s1?", history: history));

        var call = fake.Calls.Single();
        call.SystemPrompt.Should().Contain("Activity & definition reference");
        call.SystemPrompt.Should().Contain("`manualTrigger`");
        call.Conversation.Should().HaveCount(3);
        call.Conversation![0].Role.Should().Be("user");
        call.Conversation[1].Role.Should().Be("assistant");
        call.Conversation[2].Content.Should().Contain("Und s1?");
    }

    // ---- Tool-Calling (read-only MCP/Analyse-Tools) --------------------------------

    [Fact]
    public async Task StreamChat_ToolCallingDisabled_DoesNotAdvertiseTools()
    {
        var fake = new FakeLlmClient().EnqueueStream("Antwort.");
        await Run(NewService(fake), Req("Frage.")); // enableToolCalling: false (default)

        var call = fake.Calls.Single();
        call.Tools.Should().BeNull();
        call.ToolChoice.Should().BeNull();
        call.SystemPrompt.Should().NotContain("## Tools (read-only)");
    }

    [Fact]
    public async Task StreamChat_ToolCallingEnabled_AdvertisesToolsAndAutoChoice()
    {
        var fake = new FakeLlmClient().EnqueueStream("Antwort ohne Tool.");
        await Run(NewService(fake, enableToolCalling: true), Req("Frage."));

        var call = fake.Calls.Single();
        call.Tools.Should().NotBeNullOrEmpty();
        call.Tools!.Select(t => t.Name).Should().Contain("analyze_workflow");
        call.ToolChoice.Should().Be("auto");
        call.SystemPrompt.Should().Contain("## Tools (read-only)");
    }

    [Fact]
    public async Task StreamChat_ToolCall_ExecutesToolAndFeedsResultBackIntoNextTurn()
    {
        var toolCall = new LlmToolCall("call-1", "analyze_workflow", "{}");
        var fake = new FakeLlmClient()
            .EnqueueToolCallStream(new[] { toolCall }, "Ich prüfe den Workflow. ")
            .EnqueueStream("Alles in Ordnung.");

        using var doc = JsonDocument.Parse(SampleWorkflow);
        var toolCalls = new List<string>();
        var toolResults = new List<string>();
        var prose = new StringBuilder();
        ChatStreamEvent.DoneEvent? done = null;
        await foreach (var e in NewService(fake, enableToolCalling: true)
            .StreamChatAsync(Req("Prüfe."), doc.RootElement, true, false, CancellationToken.None))
        {
            switch (e)
            {
                case ChatStreamEvent.DeltaEvent d: prose.Append(d.Text); break;
                case ChatStreamEvent.ToolCallEvent tc: toolCalls.Add(tc.ToolName); break;
                case ChatStreamEvent.ToolResultEvent tr: toolResults.Add(tr.ResultJson); break;
                case ChatStreamEvent.DoneEvent dn: done = dn; break;
            }
        }

        toolCalls.Should().ContainSingle().Which.Should().Be("analyze_workflow");
        toolResults.Should().ContainSingle();
        prose.ToString().Should().Contain("Ich prüfe den Workflow.").And.Contain("Alles in Ordnung.");

        fake.Calls.Should().HaveCount(2); // two LLM rounds
        fake.Calls[0].ToolChoice.Should().Be("auto"); // round 0 is allowed to call tools
        var secondConv = fake.Calls[1].Conversation!;
        secondConv.Should().Contain(m => m.Role == "assistant" && m.ToolCalls != null && m.ToolCalls.Count == 1);
        secondConv.Should().Contain(m => m.Role == "tool" && m.ToolCallId == "call-1");

        // Token usage is SUMMED across both rounds (1/1 from the fake each) — not overwritten.
        done.Should().NotBeNull();
        done!.PromptTokens.Should().Be(2);
        done.CompletionTokens.Should().Be(2);
    }

    [Fact]
    public async Task StreamChat_ToolCall_StopsAtDepthCap()
    {
        var toolCall = new LlmToolCall("c", "analyze_workflow", "{}");
        var fake = new FakeLlmClient()
            .EnqueueToolCallStream(new[] { toolCall }, "r0 ")
            .EnqueueToolCallStream(new[] { toolCall }, "r1 "); // would keep calling the tool forever if not capped

        using var doc = JsonDocument.Parse(SampleWorkflow);
        var toolCallCount = 0;
        await foreach (var e in NewService(fake, enableToolCalling: true, maxDepth: 2)
            .StreamChatAsync(Req("x"), doc.RootElement, true, false, CancellationToken.None))
            if (e is ChatStreamEvent.ToolCallEvent) toolCallCount++;

        fake.Calls.Should().HaveCount(2); // cap = 2 LLM rounds
        toolCallCount.Should().Be(1);      // only round 0 was allowed to execute the tool
        fake.Calls[0].ToolChoice.Should().Be("auto");
        fake.Calls[0].Tools.Should().NotBeNullOrEmpty();
        fake.Calls[1].Tools.Should().BeNull();      // last round advertises no tools → forces a text answer
        fake.Calls[1].ToolChoice.Should().BeNull(); // no literal `tool_choice:none` (some providers 400 on that)
    }

    [Fact]
    public async Task StreamChat_FinalRound_ForcesToolChoiceNone_AndAnswerStillComesThrough()
    {
        var toolCall = new LlmToolCall("c", "analyze_workflow", "{}");
        var fake = new FakeLlmClient()
            .EnqueueToolCallStream(new[] { toolCall }, "Analysiere… ") // round 0 calls a tool
            .EnqueueStream("Fertige Antwort.");                        // round 1 (final, no tools) → real answer

        using var doc = JsonDocument.Parse(SampleWorkflow);
        var prose = new StringBuilder();
        var toolCallCount = 0;
        await foreach (var e in NewService(fake, enableToolCalling: true, maxDepth: 2)
            .StreamChatAsync(Req("x"), doc.RootElement, true, false, CancellationToken.None))
        {
            if (e is ChatStreamEvent.DeltaEvent d) prose.Append(d.Text);
            if (e is ChatStreamEvent.ToolCallEvent) toolCallCount++;
        }

        toolCallCount.Should().Be(1);
        prose.ToString().Should().Contain("Fertige Antwort."); // the final answer isn't empty even at the round cap
        fake.Calls[1].Tools.Should().BeNull(); // final round omits tools entirely (instead of tool_choice=none)
    }

    [Fact]
    public async Task StreamChat_ToolContext_UsesRedactedDefinition_NotOriginal()
    {
        var toolCall = new LlmToolCall("c1", "analyze_workflow", "{}");
        var fake = new FakeLlmClient()
            .EnqueueToolCallStream(new[] { toolCall }, "prüfe ")
            .EnqueueStream("ok");
        var capturing = new CapturingChatToolRegistry();

        using var doc = JsonDocument.Parse(SampleWorkflow); // contains a real secret (SUPER-SECRET)
        await foreach (var _ in NewService(fake, capturing, enableToolCalling: true)
            .StreamChatAsync(Req("x"), doc.RootElement, true, false, CancellationToken.None)) { }

        capturing.LastContext.Should().NotBeNull();
        var ctxJson = capturing.LastContext!.WorkflowDefinition.GetRawText();
        ctxJson.Should().Contain("***");
        ctxJson.Should().NotContain("SUPER-SECRET");
    }

    // ---- Execution-Log-Tools (Kontext-Gating) ---------------------------------------

    [Fact]
    public async Task StreamChat_ExecutionToolsAllowed_ContextCarriesReader()
    {
        var fake = new FakeLlmClient().EnqueueStream("ok");
        var capturing = new CapturingChatToolRegistry();
        var reader = new FakeExecutionLogReader();
        await Run(NewService(fake, capturing, enableToolCalling: true, executionLogs: reader),
            Req("Warum fehlgeschlagen?"), allowExecutionTools: true);

        capturing.LastGetToolsContext.Should().NotBeNull();
        capturing.LastGetToolsContext!.ExecutionLogs.Should().BeSameAs(reader);
        var call = fake.Calls.Single();
        call.Tools!.Select(t => t.Name).Should().Contain("list_recent_executions");
        call.SystemPrompt.Should().Contain("list_recent_executions"); // ExecutionToolsGuidance text appended to the system prompt
    }

    [Fact]
    public async Task StreamChat_ExecutionToolsNotAllowed_ContextHasNoReader()
    {
        var fake = new FakeLlmClient().EnqueueStream("ok");
        var capturing = new CapturingChatToolRegistry();
        await Run(NewService(fake, capturing, enableToolCalling: true, executionLogs: new FakeExecutionLogReader()),
            Req("Frage.")); // allowExecutionTools: false (Default)

        capturing.LastGetToolsContext.Should().NotBeNull();
        capturing.LastGetToolsContext!.ExecutionLogs.Should().BeNull();
        var call = fake.Calls.Single();
        call.Tools!.Select(t => t.Name).Should().NotContain("list_recent_executions");
        call.SystemPrompt.Should().NotContain("list_recent_executions");
    }

    [Fact]
    public async Task StreamChat_WorkflowIdNull_ContextHasNoReader()
    {
        var fake = new FakeLlmClient().EnqueueStream("ok");
        var capturing = new CapturingChatToolRegistry();
        // Unsaved workflow: WorkflowId=null — no reader is attached even if the controller allows it.
        var req = new WorkflowChatRequest("Frage.", SampleWorkflow, null, "h", Array.Empty<AiChatTurnDto>());

        using var doc = JsonDocument.Parse(SampleWorkflow);
        await foreach (var _ in NewService(fake, capturing, enableToolCalling: true, executionLogs: new FakeExecutionLogReader())
            .StreamChatAsync(req, doc.RootElement, true, true, CancellationToken.None)) { }

        capturing.LastGetToolsContext!.ExecutionLogs.Should().BeNull();
    }

    [Fact]
    public async Task StreamChat_ListRecentExecutionsToolCall_RoundTripsResultIntoConversation()
    {
        var execId = Guid.NewGuid();
        var reader = new FakeExecutionLogReader();
        reader.Executions.Add(FakeExecutionLogReader.Summary(execId, "Failed", errorMessage: "Boom"));
        var toolCall = new LlmToolCall("call-9", "list_recent_executions", "{}");
        var fake = new FakeLlmClient()
            .EnqueueToolCallStream(new[] { toolCall }, "Ich schaue nach. ")
            .EnqueueStream("Der letzte Lauf schlug fehl.");

        using var doc = JsonDocument.Parse(SampleWorkflow);
        await foreach (var _ in NewService(fake, enableToolCalling: true, executionLogs: reader)
            .StreamChatAsync(Req("Warum fehlgeschlagen?"), doc.RootElement, true, true, CancellationToken.None)) { }

        fake.Calls.Should().HaveCount(2);
        var toolTurn = fake.Calls[1].Conversation!.Single(m => m.Role == "tool" && m.ToolCallId == "call-9");
        toolTurn.Content.Should().Contain(execId.ToString());
        toolTurn.Content.Should().Contain("Boom");
    }

    /// <summary>Test double that captures the <see cref="ChatToolContext"/> it was called with
    /// (both when advertising tools via <see cref="GetTools"/> and when executing one).</summary>
    private sealed class CapturingChatToolRegistry : IChatToolRegistry
    {
        private readonly WorkflowChatToolRegistry _inner = new();
        public ChatToolContext? LastContext { get; private set; }
        public ChatToolContext? LastGetToolsContext { get; private set; }
        public IReadOnlyList<LlmToolDefinition> GetTools(ChatToolContext context)
        {
            LastGetToolsContext = context;
            return _inner.GetTools(context);
        }
        public Task<string> ExecuteAsync(string name, string argumentsJson, ChatToolContext context, CancellationToken ct)
        {
            LastContext = context;
            return Task.FromResult("{\"ok\":true}");
        }
    }
}
