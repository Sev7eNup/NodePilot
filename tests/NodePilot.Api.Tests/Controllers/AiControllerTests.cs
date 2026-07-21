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
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public class AiControllerTests
{
    private const string MinimalEnvelope = """
        {
          "name": "Generated",
          "description": "Test",
          "definition": {
            "nodes": [
              { "id": "n1", "type": "activity", "position": { "x": 0, "y": 0 },
                "data": { "label": "Start", "activityType": "manualTrigger", "config": {} } }
            ],
            "edges": []
          }
        }
        """;

    private static (AiController controller, CapturingAuditWriter audit, FakeLlmClient llm, MemoryStream body)
        NewController(bool enabled = true, string role = "Operator")
    {
        var options = new StaticOptionsMonitor<LlmOptions>(new LlmOptions
        {
            Enabled = enabled,
            BaseUrl = "http://localhost/v1",
            Model = "test-model",
            MaxTokens = 100,
            TimeoutSeconds = 30,
        });
        var prompts = new PromptCatalog();
        var llm = new FakeLlmClient();
        var scriptGen = new ScriptGenerationService(llm, prompts);
        var workflowGen = new WorkflowGenerationService(llm, prompts);
        var audit = new CapturingAuditWriter();
        var controller = new AiController(options, scriptGen, workflowGen, audit, NullLogger<AiController>.Instance);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Role, role), new Claim(ClaimTypes.Name, "tester") }, "TestAuth"));
        var body = new MemoryStream();
        var ctx = new DefaultHttpContext { User = principal };
        ctx.Response.Body = body;
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return (controller, audit, llm, body);
    }

    private static GenerateScriptRequest ScriptReq(string prompt = "echo hello",
        IReadOnlyList<UpstreamVariableDto>? vars = null, string? currentScript = null)
        => new(prompt, Guid.NewGuid(), "step-x", vars ?? Array.Empty<UpstreamVariableDto>(), currentScript);

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

    // ---- generate-script (SSE) ------------------------------------------------------

    [Fact]
    public async Task GenerateScript_WhenDisabled_Returns503()
    {
        var (controller, _, _, _) = NewController(enabled: false);
        var result = await controller.GenerateScript(ScriptReq(), CancellationToken.None);
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task GenerateScript_EmptyPrompt_Returns400()
    {
        var (controller, _, _, _) = NewController();
        (await controller.GenerateScript(ScriptReq(prompt: "  "), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GenerateScript_HappyPath_StreamsAndFiresAudit()
    {
        var (controller, audit, llm, body) = NewController();
        llm.EnqueueStream("Get-Service ", "-Name Spooler");

        var result = await controller.GenerateScript(ScriptReq(), CancellationToken.None);

        result.Should().BeOfType<EmptyResult>();
        controller.Response.ContentType.Should().Be("text/event-stream");
        var events = ParseSse(body);
        events.Should().Contain(e => e.ev == "delta");
        events.Should().Contain(e => e.ev == "done");

        var auditCall = audit.Calls.Should().ContainSingle(c => c.Action == "AI_SCRIPT_GENERATED").Subject;
        auditCall.Details.Should().Contain("\"responseChars\"");
        auditCall.Details.Should().NotContain("echo hello"); // the prompt itself is never written to the audit log
    }

    [Theory]
    [InlineData(LlmErrorKind.Unreachable, 503, "LLM_UNREACHABLE")]
    [InlineData(LlmErrorKind.Timeout, 503, "LLM_TIMEOUT")]
    [InlineData(LlmErrorKind.MalformedResponse, 502, "LLM_MALFORMED_RESPONSE")]
    [InlineData(LlmErrorKind.UpstreamError, 502, "LLM_UPSTREAM_ERROR")]
    public async Task GenerateScript_PreStreamLlmException_MappedToHttp(
        LlmErrorKind kind, int expectedStatus, string expectedCode)
    {
        var (controller, audit, llm, _) = NewController();
        llm.EnqueueStreamException(new LlmException(kind, "boom", httpStatus: 502));

        var result = await controller.GenerateScript(ScriptReq(), CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(expectedStatus);
        obj.Value!.GetType().GetProperty("code")!.GetValue(obj.Value).Should().Be(expectedCode);
        audit.Calls.Should().BeEmpty();
    }

    // ---- generate-workflow (unchanged JSON) -----------------------------------------

    [Fact]
    public async Task GenerateWorkflow_WhenDisabled_Returns503()
    {
        var (controller, _, _, _) = NewController(enabled: false);
        var result = await controller.GenerateWorkflow(new GenerateWorkflowRequest("smoke"), CancellationToken.None);
        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task GenerateWorkflow_EmptyPrompt_Returns400()
    {
        var (controller, _, _, _) = NewController();
        var result = await controller.GenerateWorkflow(new GenerateWorkflowRequest(""), CancellationToken.None);
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GenerateWorkflow_HappyPath_Returns200AndFiresAudit()
    {
        var (controller, audit, llm, _) = NewController();
        llm.EnqueueContent(MinimalEnvelope);

        var result = await controller.GenerateWorkflow(new GenerateWorkflowRequest("smoke please"), CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var resp = ok.Value.Should().BeOfType<GenerateWorkflowResponse>().Subject;
        resp.SuggestedName.Should().Be("Generated");
        resp.NodeCount.Should().Be(1);

        audit.Calls.Should().ContainSingle(c => c.Action == "AI_WORKFLOW_GENERATED");
    }

    [Fact]
    public async Task GenerateWorkflow_DoubleFail_Returns502LlmMalformedResponse()
    {
        var (controller, audit, llm, _) = NewController();
        llm.EnqueueContent("nope").EnqueueContent("still nope");

        var result = await controller.GenerateWorkflow(new GenerateWorkflowRequest("smoke"), CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        obj.Value!.GetType().GetProperty("code")!.GetValue(obj.Value).Should().Be("LLM_MALFORMED_RESPONSE");
        audit.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// Hot-reload: AiController reads IOptionsMonitor&lt;LlmOptions&gt;.CurrentValue per request, so
    /// toggling Llm:Enabled in the Settings UI flips the 503 gate live without a restart. Drive the
    /// monitor (test stand-in for a reloadOnChange config reload) from disabled→enabled between two
    /// requests on the SAME controller instance and assert the gate flips.
    /// </summary>
    [Fact]
    public async Task GenerateScript_DisabledGate_FlipsLiveAfterConfigReload()
    {
        var monitor = new MutableOptionsMonitor<LlmOptions>(new LlmOptions
        {
            Enabled = false,
            BaseUrl = "http://localhost/v1",
            Model = "test-model",
            MaxTokens = 100,
            TimeoutSeconds = 30,
        });
        var prompts = new PromptCatalog();
        var llm = new FakeLlmClient();
        var scriptGen = new ScriptGenerationService(llm, prompts);
        var workflowGen = new WorkflowGenerationService(llm, prompts);
        var audit = new CapturingAuditWriter();
        var controller = new AiController(monitor, scriptGen, workflowGen, audit, NullLogger<AiController>.Instance);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Role, "Operator"), new Claim(ClaimTypes.Name, "tester") }, "TestAuth"));
        var body = new MemoryStream();
        controller.ControllerContext = new ControllerContext
        { HttpContext = new DefaultHttpContext { User = principal, Response = { Body = body } } };

        // Disabled: gate returns 503 before the LLM is touched.
        (await controller.GenerateScript(ScriptReq(), CancellationToken.None))
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
        llm.EnqueueStream("Get-Service ", "-Name Spooler");

        // Same controller instance: the next request now streams (no 503).
        var result = await controller.GenerateScript(ScriptReq(), CancellationToken.None);
        result.Should().BeOfType<EmptyResult>();
        controller.Response.ContentType.Should().Be("text/event-stream");
    }

    /// <summary>Minimal immutable IOptionsMonitor test double for the hot-reload swap.</summary>

    /// <summary>
    /// Settable IOptionsMonitor test double for hot-reload tests — mutating CurrentValue fans
    /// out to registered OnChange listeners, mirroring a real reloadOnChange config reload.
    /// </summary>
}
