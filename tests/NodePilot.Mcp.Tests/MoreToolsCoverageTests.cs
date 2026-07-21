using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using NodePilot.Mcp.Tests.Infra;
using NodePilot.Mcp.Tools;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Mcp.Tests;

/// <summary>Coverage for telemetry, supporting-data, destructive and remaining canvas tools.</summary>
public sealed class MoreToolsCoverageTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string J(object o) => JsonSerializer.Serialize(o, Web);
    private static JsonElement E(string s) => JsonDocument.Parse(s).RootElement;

    // ---- Telemetry gaps -----------------------------------------------------

    [Fact]
    public async Task Coverage_StepHealth_StepStats_Work()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/coverage").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                workflowId = id, windowDays = 30, totalExecutions = 5, oldestExecutionInWindow = (DateTime?)DateTime.UtcNow,
                nodes = new[] { new { stepId = "n1", executedCount = 5, failedCount = 1, skippedCount = 0, lastExecutedAt = (DateTime?)DateTime.UtcNow, lastSucceededAt = (DateTime?)DateTime.UtcNow, lastFailedAt = (DateTime?)null } },
            }));
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/step-health").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new Dictionary<string, object> { ["n1"] = new[] { new { status = "Succeeded", startedAt = (DateTime?)DateTime.UtcNow } } }));
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/step-stats").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new Dictionary<string, object> { ["n1"] = new { totalRuns = 5, failedRuns = 1, failureRate = 0.2, avgDurationMs = 100L, p95DurationMs = 200L, lastDurationMs = 90L } }));

        var tools = new TelemetryTools(api.Client());
        J(await tools.GetWorkflowCoverage(id.ToString())).Should().Contain("n1");
        J(await tools.GetWorkflowStepHealth(id.ToString())).Should().Contain("Succeeded");
        J(await tools.GetWorkflowStepStats(id.ToString())).Should().Contain("failureRate");
    }

    // ---- Supporting gaps ----------------------------------------------------

    [Fact]
    public async Task GetMachine_CreateMachine_TestMachine_Work()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        object machine = new
        {
            id, name = "WEB01", hostname = "web01", winRmPort = 5985, useSsl = false, defaultCredentialId = (Guid?)null, tags = (string?)null,
            lastConnectivityCheck = (DateTime?)null, isReachable = true, usedByWorkflowCount = 0, recentStepCount = 0, recentFailedStepCount = 0, activeRunCount = 0,
        };
        api.Server.Given(Request.Create().WithPath($"/api/machines/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(machine));
        api.Server.Given(Request.Create().WithPath("/api/machines").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(machine));
        api.Server.Given(Request.Create().WithPath($"/api/machines/{id}/test").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { success = true, computerName = "WEB01", error = (string?)null, credentialUsed = "svc" }));

        var tools = new SupportingDataTools(api.Client());
        J(await tools.GetMachine(id.ToString())).Should().Contain("WEB01");
        J(await tools.CreateMachine("WEB01", "web01")).Should().Contain("\"created\":true");
        J(await tools.TestMachine(id.ToString())).Should().Contain("\"success\":true");
    }

    [Fact]
    public async Task GetCredential_CreateGlobal_UpdateCredentialMergesDomain()
    {
        var cid = Guid.NewGuid();
        var gid = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/credentials/{cid}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = cid, name = "Svc", username = "svc", domain = "CORP" }));
        api.Server.Given(Request.Create().WithPath($"/api/credentials/{cid}").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(204));
        api.Server.Given(Request.Create().WithPath("/api/global-variables").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new { id = gid, name = "FLAG", value = "1", isSecret = false, description = (string?)null, createdAt = DateTime.UtcNow, updatedAt = DateTime.UtcNow, updatedBy = "admin" }));

        var tools = new SupportingDataTools(api.Client());
        J(await tools.GetCredential(cid.ToString())).Should().Contain("Svc");
        J(await tools.CreateGlobalVariable("FLAG", "1")).Should().Contain("\"created\":true");

        // Rename only — domain "CORP" must be preserved (read-modify-write).
        await tools.UpdateCredential(cid.ToString(), name: "Svc2");
        var body = api.Server.LogEntries.Last(e => e.RequestMessage.Method == "PUT").RequestMessage.Body ?? "";
        body.Should().Contain("Svc2").And.Contain("CORP");
    }

    // ---- Destructive (method-level; registration gating tested in smoke) -----

    [Fact]
    public async Task DestructiveTools_DeleteAndForceUnlock_Work()
    {
        var wf = Guid.NewGuid();
        var mid = Guid.NewGuid();
        var cid = Guid.NewGuid();
        var gid = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{wf}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(TestApi.WorkflowResponse(wf, "WF", "{}")));
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{wf}").UsingDelete()).RespondWith(Response.Create().WithStatusCode(204));
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{wf}/force-unlock").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(TestApi.WorkflowResponse(wf, "WF", "{}")));
        api.Server.Given(Request.Create().WithPath($"/api/machines/{mid}").UsingDelete()).RespondWith(Response.Create().WithStatusCode(204));
        api.Server.Given(Request.Create().WithPath($"/api/credentials/{cid}").UsingDelete()).RespondWith(Response.Create().WithStatusCode(204));
        api.Server.Given(Request.Create().WithPath($"/api/global-variables/{gid}").UsingDelete()).RespondWith(Response.Create().WithStatusCode(204));
        var fid = Guid.NewGuid();
        api.Server.Given(Request.Create().WithPath($"/api/global-variable-folders/{fid}").UsingDelete()).RespondWith(Response.Create().WithStatusCode(204));

        var tools = new DestructiveTools(api.Client());
        J(await tools.DeleteWorkflow(wf.ToString())).Should().Contain("\"deleted\":true");
        J(await tools.ForceUnlockWorkflow(wf.ToString())).Should().Contain("\"forceUnlocked\":true");
        J(await tools.DeleteMachine(mid.ToString())).Should().Contain("\"deleted\":true");
        J(await tools.DeleteCredential(cid.ToString())).Should().Contain("\"deleted\":true");
        J(await tools.DeleteGlobalVariable(gid.ToString())).Should().Contain("\"deleted\":true");
        J(await tools.DeleteGlobalVariableFolder(fid.ToString())).Should().Contain("\"deleted\":true");
    }

    // ---- Canvas gaps --------------------------------------------------------

    [Fact]
    public void CheckStyleguide_FlagsMissingLabelAndNoTrigger()
    {
        using var api = new TestApi();
        var def = """{"nodes":[{"id":"a","type":"activity","data":{"activityType":"runScript","config":{}}}],"edges":[]}""";
        var json = J(new CanvasAssistantTools(api.Client()).CheckStyleguide(E(def)));
        json.Should().Contain("\"ok\":false");
        json.Should().Contain("no label");
        json.Should().Contain("trigger");
        json.Should().Contain("nodepilot://styleguide");
    }

    [Fact]
    public async Task GetWorkflowNode_ReturnsRedactedNode_AndErrorsOnMissing()
    {
        var id = Guid.NewGuid();
        var def = "{\"nodes\":[{\"id\":\"n1\",\"type\":\"activity\",\"data\":{\"activityType\":\"restApi\",\"config\":{\"apiKey\":\"sk-live\",\"url\":\"https://x\"}}}],\"edges\":[]}";
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(TestApi.WorkflowResponse(id, "WF", def)));

        var tools = new CanvasAssistantTools(api.Client());
        var json = J(await tools.GetWorkflowNode(id.ToString(), "n1"));
        json.Should().Contain("https://x");
        json.Should().NotContain("sk-live");
        json.Should().Contain("***");

        var act = async () => await tools.GetWorkflowNode(id.ToString(), "ghost");
        await act.Should().ThrowAsync<Exception>();
    }
}
