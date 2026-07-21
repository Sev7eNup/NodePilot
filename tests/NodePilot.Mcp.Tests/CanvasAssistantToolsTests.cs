using System.Text.Json;
using FluentAssertions;
using NodePilot.Mcp.Tests.Infra;
using NodePilot.Mcp.Tools;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Mcp.Tests;

public sealed class CanvasAssistantToolsTests
{
    private static JsonElement E(string s) => JsonDocument.Parse(s).RootElement;

    // Serialize the way the MCP SDK serializes tool results to the agent: camelCase + relaxed
    // escaping (so apostrophes/em-dashes are literal). Keeps assertions aligned with real output.
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static string J(object o) => JsonSerializer.Serialize(o, Web);

    // manualTrigger n0 → runScript n1 → remote fileOperation n2 (no machine); n9 disconnected.
    private const string Master = """
    {
      "nodes": [
        { "id": "n0", "type": "activity", "data": { "activityType": "manualTrigger", "label": "Start",
          "config": { "parameters": [ { "name": "env" } ] } } },
        { "id": "n1", "type": "activity", "data": { "activityType": "runScript", "label": "Probe",
          "config": { "script": "$hostName = hostname" } } },
        { "id": "n2", "type": "activity", "data": { "activityType": "fileOperation", "label": "Copy",
          "config": { "operation": "copy" } } },
        { "id": "n9", "type": "activity", "data": { "activityType": "log", "label": "Orphan", "config": {} } }
      ],
      "edges": [
        { "id": "e0", "source": "n0", "target": "n1" },
        { "id": "e1", "source": "n1", "target": "n2" }
      ]
    }
    """;

    private static CanvasAssistantTools Tools(TestApi api) => new(api.Client());

    [Fact]
    public void AnalyzeWorkflow_FlagsOrphanAndRemoteWithoutMachine_ButHasRoot()
    {
        using var api = new TestApi();
        var json = J(Tools(api).AnalyzeWorkflow(E(Master)));

        json.Should().Contain("\"n0\"");          // trigger root present
        json.Should().Contain("never runs");       // n9 orphan
        json.Should().Contain("n9");
        json.Should().Contain("no targetMachineId"); // n2 remote without machine
        json.Should().Contain("\"ok\":true");       // only warnings, no errors
    }

    [Fact]
    public void AnalyzeWorkflow_NoTrigger_IsError()
    {
        using var api = new TestApi();
        var noTrigger = """{"nodes":[{"id":"a","type":"activity","data":{"activityType":"runScript","label":"x","config":{}}}],"edges":[]}""";
        var json = J(Tools(api).AnalyzeWorkflow(E(noTrigger)));
        json.Should().Contain("No active trigger");
        json.Should().Contain("\"ok\":false");
    }

    [Fact]
    public void AnalyzeWorkflow_DetectsCycle()
    {
        using var api = new TestApi();
        var cyclic = """
        {"nodes":[
          {"id":"t","type":"activity","data":{"activityType":"manualTrigger","label":"t","config":{}}},
          {"id":"a","type":"activity","data":{"activityType":"log","label":"a","config":{}}},
          {"id":"b","type":"activity","data":{"activityType":"log","label":"b","config":{}}}],
         "edges":[
          {"id":"e0","source":"t","target":"a"},
          {"id":"e1","source":"a","target":"b"},
          {"id":"e2","source":"b","target":"a"}]}
        """;
        var json = J(Tools(api).AnalyzeWorkflow(E(cyclic)));
        json.Should().Contain("Cycle detected");
    }

    [Fact]
    public void FindUnresolvedReferences_CatchesWrongTailAndUnknownVariable()
    {
        using var api = new TestApi();
        var def = """
        {"nodes":[
          {"id":"check","type":"activity","data":{"activityType":"runScript","label":"c","config":{"script":"$x = 1"}}},
          {"id":"use","type":"activity","data":{"activityType":"log","label":"u","config":{"message":"free {{check.freeGb}} and {{ghost.output}}"}}}],
         "edges":[]}
        """;
        var json = J(Tools(api).FindUnresolvedReferences(E(def)));
        json.Should().Contain("check.param.freeGb");   // hint to the correct form
        json.Should().Contain("Unknown variable 'ghost'");
    }

    [Fact]
    public async Task GetAvailableVariables_ListsUpstreamOutputsParamsAndManual()
    {
        using var api = new TestApi();
        // globals endpoint left unstubbed → best-effort fetch fails gracefully.
        var json = J(await Tools(api).GetAvailableVariables(E(Master), "n2"));

        json.Should().Contain("{{n1.output}}");
        json.Should().Contain("{{n1.param.exitCode}}");   // static catalog output
        json.Should().Contain("{{n1.param.hostName}}");   // dynamic runScript $var
        json.Should().Contain("{{manual.env}}");          // run-level from manualTrigger
    }

    [Fact]
    public void DiffWorkflowDefinition_ReportsAddedRemovedModified()
    {
        using var api = new TestApi();
        var current = """{"nodes":[{"id":"a","x":1},{"id":"b","x":1}],"edges":[]}""";
        var proposed = """{"nodes":[{"id":"a","x":2},{"id":"c","x":1}],"edges":[]}""";
        var json = J(Tools(api).DiffWorkflowDefinition(E(current), E(proposed)));
        json.Should().Contain("\"added\":[\"c\"]");
        json.Should().Contain("\"removed\":[\"b\"]");
        json.Should().Contain("\"modified\":[\"a\"]");
    }

    [Fact]
    public void ValidateEdgeCondition_AcceptsValid_RejectsBadType()
    {
        using var api = new TestApi();
        var ok = J(Tools(api).ValidateEdgeCondition(E("""{"type":"comparison","op":"=="}""")));
        ok.Should().Contain("\"isValid\":true");

        var bad = J(Tools(api).ValidateEdgeCondition(E("""{"type":"frobnicate"}""")));
        bad.Should().Contain("\"isValid\":false");
    }

    [Fact]
    public void ValidateActivityConfig_RunScriptMissingScript_FlagsRequired()
    {
        using var api = new TestApi();
        var json = J(Tools(api).ValidateActivityConfig("runScript", E("""{"timeoutSeconds":30}""")));
        json.Should().Contain("\"knownType\":true");
        json.Should().Contain("script"); // missingRequired
    }

    [Fact]
    public void SuggestLayout_AssignsPositions_AndRedactsSecrets()
    {
        using var api = new TestApi();
        var def = """
        {"nodes":[
          {"id":"t","type":"activity","data":{"activityType":"manualTrigger","label":"t","config":{}}},
          {"id":"h","type":"activity","data":{"activityType":"webhookTrigger","label":"h","config":{"secret":"top-secret"}}}],
         "edges":[]}
        """;
        var json = J(Tools(api).SuggestLayout(E(def)));
        json.Should().Contain("position");
        json.Should().NotContain("top-secret");
        json.Should().Contain("***");
    }

    [Fact]
    public void PreviewTemplateResolution_ResolvesMocks_ReportsMissing()
    {
        using var api = new TestApi();
        var json = J(Tools(api).PreviewTemplateResolution(
            "host {{n1.param.hostName}} and {{ghost.x}}",
            new Dictionary<string, string> { ["n1.param.hostName"] = "WEB01" }));
        json.Should().Contain("WEB01");
        json.Should().Contain("{{ghost.x}}");
    }

    [Fact]
    public void AnalyzeWorkflow_HybridRunScriptWithoutMachine_IsNotWarned()
    {
        using var api = new TestApi();
        // runScript without a machine is HYBRID (runs locally) → must NOT be flagged.
        var def = """
        {"nodes":[
          {"id":"t","type":"activity","data":{"activityType":"manualTrigger","label":"t","config":{}}},
          {"id":"r","type":"activity","data":{"activityType":"runScript","label":"r","config":{"script":"x"}}}],
         "edges":[{"id":"e","source":"t","target":"r"}]}
        """;
        var json = J(Tools(api).AnalyzeWorkflow(E(def)));
        json.Should().NotContain("targetMachineId"); // no false remote warning
        json.Should().Contain("\"ok\":true");
    }

    [Fact]
    public void SuggestLayout_CyclicGraph_Terminates()
    {
        using var api = new TestApi();
        // t → a → b → a (cycle reachable from a trigger). Must not hang.
        var cyclic = """
        {"nodes":[
          {"id":"t","type":"activity","data":{"activityType":"manualTrigger","label":"t","config":{}}},
          {"id":"a","type":"activity","data":{"activityType":"log","label":"a","config":{}}},
          {"id":"b","type":"activity","data":{"activityType":"log","label":"b","config":{}}}],
         "edges":[
          {"id":"e0","source":"t","target":"a"},
          {"id":"e1","source":"a","target":"b"},
          {"id":"e2","source":"b","target":"a"}]}
        """;
        var json = J(Tools(api).SuggestLayout(E(cyclic)));
        json.Should().Contain("position");
    }

    [Fact]
    public async Task GetAvailableVariables_IncludesTriggerReturnDataAndRegistry()
    {
        using var api = new TestApi();
        // webhookTrigger (static outputs) → registryOperation read (dynamic) → returnData → target.
        var def = """
        {"nodes":[
          {"id":"hook","type":"activity","data":{"activityType":"webhookTrigger","label":"h","config":{}}},
          {"id":"reg","type":"activity","data":{"activityType":"registryOperation","label":"r","outputVariable":"reg","config":{"operation":"read","valueName":"Build"}}},
          {"id":"ret","type":"activity","data":{"activityType":"returnData","label":"rd","config":{"data":{"result":"ok"}}}},
          {"id":"end","type":"activity","data":{"activityType":"log","label":"e","config":{}}}],
         "edges":[
          {"id":"e0","source":"hook","target":"reg"},
          {"id":"e1","source":"reg","target":"ret"},
          {"id":"e2","source":"ret","target":"end"}]}
        """;
        var json = J(await Tools(api).GetAvailableVariables(E(def), "end"));
        json.Should().Contain("{{hook.param.webhookBody}}");   // trigger static output (was skipped before)
        json.Should().Contain("{{reg.param.value}}");          // registryOperation read+valueName dynamic
        json.Should().Contain("{{ret.param.result}}");         // returnData key
    }

    [Fact]
    public void ValidateActivityConfig_RestApi_NowCoveredByReference()
    {
        using var api = new TestApi();
        // restApi is now in the curated reference → url is required.
        var json = J(Tools(api).ValidateActivityConfig("restApi", E("""{"method":"GET"}""")));
        json.Should().Contain("\"hasConfigReference\":true");
        json.Should().Contain("url"); // missingRequired
    }

    [Fact]
    public async Task PreviewTemplateResolution_UsesStepTestContext()
    {
        var wf = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{wf}/steps/n2/test-context").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                executionId = (Guid?)Guid.NewGuid(), executedAt = (DateTime?)DateTime.UtcNow, status = "Succeeded",
                variables = new[]
                {
                    new { key = "n1.param.hostName", origin = "n1", source = "param", value = "WEB42" },
                },
            }));

        var json = J(await Tools(api).PreviewTemplateResolution(
            "host {{n1.param.hostName}}", mockVariables: null, workflowId: wf.ToString(), stepId: "n2"));
        json.Should().Contain("WEB42");        // pulled from the real run context
        json.Should().Contain("contextNote");
    }

    [Fact]
    public async Task GetFailureContext_ReturnsLatestFailedStepError()
    {
        var wf = Guid.NewGuid();
        var exec = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/executions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
            {
                new { id = exec, workflowId = wf, status = "Failed", startedAt = DateTime.UtcNow,
                      completedAt = (DateTime?)DateTime.UtcNow, triggeredBy = "manual", errorMessage = "boom" },
            }));
        api.Server.Given(Request.Create().WithPath($"/api/executions/{exec}/steps").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
            {
                new { id = Guid.NewGuid(), stepId = "n1", stepName = "Probe", stepType = "runScript",
                      targetMachine = (string?)null, status = "Failed", startedAt = (DateTime?)DateTime.UtcNow,
                      completedAt = (DateTime?)DateTime.UtcNow, output = (string?)null, errorOutput = "access denied",
                      attemptCount = 1, pausedAt = (DateTime?)null, variablesSnapshot = (string?)null, traceOutput = (string?)null },
            }));

        var json = J(await Tools(api).GetFailureContext(wf.ToString()));
        json.Should().Contain("access denied");
        json.Should().Contain("n1");
    }
}
