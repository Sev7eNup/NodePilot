using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Ai;
using NodePilot.Ai.Knowledge;
using NodePilot.Api.Controllers;
using NodePilot.Api.Security;
using NodePilot.Core.Interfaces;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public class AiKnowledgeControllerTests
{
    private static (AiKnowledgeController controller, CapturingAuditWriter audit, FakeLlmClient llm, MemoryStream body)
        Build(bool llmEnabled = true, bool knowledgeEnabled = true, bool docs = true, bool op = true, bool src = false,
              string role = "Operator", bool enableToolCalling = false)
    {
        var llmOptions = new StaticOptionsMonitor<LlmOptions>(new LlmOptions
        {
            Enabled = llmEnabled, BaseUrl = "http://localhost/v1", Model = "test-model",
            MaxTokens = 100, TimeoutSeconds = 30, EnableToolCalling = enableToolCalling,
        });
        var kOptions = new StaticOptionsMonitor<AiKnowledgeOptions>(new AiKnowledgeOptions
        {
            Enabled = knowledgeEnabled, DocsEnabled = docs, OperationalEnabled = op, SourceCodeEnabled = src,
        });
        var llm = new FakeLlmClient();
        var db = TestDbFactory.Create();
        var registry = new KnowledgeChatToolRegistry(new DocsKnowledgeReader(kOptions), new SourceCodeKnowledgeReader(kOptions));
        var operational = new OperationalKnowledgeReader(db, new StubAuditDetailsRedactor());
        var assistant = new KnowledgeAssistantService(llm, new PromptCatalog(), registry, llmOptions, kOptions, operational, new StubSettingsKnowledgeReader());
        var audit = new CapturingAuditWriter();
        var authz = new ResourceAuthorizationService(db);
        var controller = new AiKnowledgeController(llmOptions, kOptions, assistant, authz, audit, NullLogger<AiKnowledgeController>.Instance);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Role, role), new Claim(ClaimTypes.Name, "tester") }, "TestAuth"));
        var body = new MemoryStream();
        var ctx = new DefaultHttpContext { User = principal };
        ctx.Response.Body = body;
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return (controller, audit, llm, body);
    }

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

    private static KnowledgeCapabilitiesDto Caps(AiKnowledgeController c) =>
        (c.Capabilities().Result as OkObjectResult)!.Value as KnowledgeCapabilitiesDto
        ?? throw new InvalidOperationException("capabilities not returned");

    // These controller tests never exercise read_settings (tool-calling is off by default); an empty
    // snapshot keeps the assistant constructible.
    private sealed class StubSettingsKnowledgeReader : ISettingsKnowledgeReader
    {
        public IReadOnlyList<SettingsSectionKnowledge> GetRedactedSnapshot() => Array.Empty<SettingsSectionKnowledge>();
    }

    // ---- 503 gating ----------------------------------------------------------------------------

    [Fact]
    public async Task Ask_WhenLlmDisabled_Returns503_LlmDisabled()
    {
        var (controller, _, _, _) = Build(llmEnabled: false);
        var result = await controller.Ask(new KnowledgeAskRequest("hi", null), CancellationToken.None);
        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        obj.Value!.GetType().GetProperty("code")!.GetValue(obj.Value).Should().Be("LLM_DISABLED");
    }

    [Fact]
    public async Task Ask_WhenKnowledgeDisabled_Returns503_KnowledgeDisabled()
    {
        var (controller, _, _, _) = Build(llmEnabled: true, knowledgeEnabled: false);
        var result = await controller.Ask(new KnowledgeAskRequest("hi", null), CancellationToken.None);
        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        obj.Value!.GetType().GetProperty("code")!.GetValue(obj.Value).Should().Be("KNOWLEDGE_DISABLED");
    }

    [Fact]
    public async Task Ask_EmptyQuestion_Returns400()
    {
        var (controller, _, _, _) = Build();
        (await controller.Ask(new KnowledgeAskRequest("   ", null), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Ask_HistoryTooLong_Returns400()
    {
        var (controller, _, _, _) = Build();
        var history = Enumerable.Range(0, 25)
            .Select(i => new AiChatTurnDto(i % 2 == 0 ? "user" : "assistant", $"turn {i}")).ToArray();
        var result = await controller.Ask(new KnowledgeAskRequest("q", history), CancellationToken.None);
        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.GetType().GetProperty("code")!.GetValue(bad.Value).Should().Be("HISTORY_TOO_LONG");
    }

    // ---- Streaming happy path + audit ----------------------------------------------------------

    [Fact]
    public async Task Ask_Enabled_StreamsDeltasAndDone_FiresAudit()
    {
        var (controller, audit, llm, body) = Build(role: "Viewer");
        llm.EnqueueStream("NodePilot ", "ist eine Orchestrierungs-Plattform.");

        var result = await controller.Ask(new KnowledgeAskRequest("Was ist NodePilot?", null), CancellationToken.None);

        result.Should().BeOfType<EmptyResult>();
        controller.Response.ContentType.Should().Be("text/event-stream");
        var events = ParseSse(body);
        events.Should().Contain(e => e.ev == "delta");
        events.Should().Contain(e => e.ev == "done");
        events.Should().NotContain(e => e.ev == "proposal" || e.ev == "building");

        var call = audit.Calls.Should().ContainSingle(c => c.Action == "AI_KNOWLEDGE_ASKED").Subject;
        call.Details.Should().NotContain("Was ist NodePilot?"); // question text is never audited
    }

    [Fact]
    public async Task Ask_ForwardsTimeZone_IntoSystemPromptTimeContext()
    {
        var (controller, _, llm, _) = Build(role: "Viewer");
        llm.EnqueueStream("ok");

        await controller.Ask(
            new KnowledgeAskRequest("Wie spät ist es?", null, "Europe/Berlin", 120),
            CancellationToken.None);

        // The caller's zone must reach the model as a "current time" anchor in the system prompt.
        var systemPrompt = llm.Calls.Should().ContainSingle().Subject.SystemPrompt;
        systemPrompt.Should().Contain("Aktueller Zeitpunkt");
        systemPrompt.Should().Contain("Europe/Berlin");
        systemPrompt.Should().Contain("get_next_scheduled_fires");
    }

    [Fact]
    public async Task Ask_PreStreamLlmError_MappedToHttp_NoAudit()
    {
        var (controller, audit, llm, _) = Build();
        llm.EnqueueStreamException(new LlmException(LlmErrorKind.Timeout, "slow"));

        var result = await controller.Ask(new KnowledgeAskRequest("q", null), CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        audit.Calls.Should().BeEmpty();
    }

    // ---- Capabilities --------------------------------------------------------------------------

    [Fact]
    public void Capabilities_AllEnabledPrivileged_AllTrue()
    {
        var (controller, _, _, _) = Build(src: true, role: "Admin");
        var caps = Caps(controller);
        caps.Enabled.Should().BeTrue();
        caps.Docs.Should().BeTrue();
        caps.Operational.Should().BeTrue();
        caps.SourceCode.Should().BeTrue();
    }

    [Fact]
    public void Capabilities_LlmDisabled_EverythingFalse()
    {
        var (controller, _, _, _) = Build(llmEnabled: false, src: true, role: "Admin");
        var caps = Caps(controller);
        caps.Enabled.Should().BeFalse();
        caps.Docs.Should().BeFalse();
        caps.Operational.Should().BeFalse();
        caps.SourceCode.Should().BeFalse();
    }

    [Fact]
    public void Capabilities_Viewer_SourceCodeFalse_EvenWhenEnabled()
    {
        var (controller, _, _, _) = Build(src: true, role: "Viewer");
        var caps = Caps(controller);
        caps.Enabled.Should().BeTrue();
        caps.Docs.Should().BeTrue();
        caps.SourceCode.Should().BeFalse(); // source-code is Admin/Operator only
    }

    [Fact]
    public void Capabilities_PerSourceToggleReflected()
    {
        var (controller, _, _, _) = Build(docs: true, op: false, src: true, role: "Admin");
        var caps = Caps(controller);
        caps.Docs.Should().BeTrue();
        caps.Operational.Should().BeFalse();
        caps.SourceCode.Should().BeTrue();
    }
}
