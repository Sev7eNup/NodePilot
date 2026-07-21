using System.Text.Json;
using FluentAssertions;
using NodePilot.Mcp.Tests.Infra;
using NodePilot.Mcp.Tools;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Mcp.Tests;

public sealed class WorkflowEditToolsTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    private static object Wf(Guid id, string name, string definitionJson) => new
    {
        id, name, description = (string?)null, definitionJson, version = 1, isEnabled = true,
        createdAt = DateTime.UtcNow, updatedAt = DateTime.UtcNow, createdBy = "admin", updatedBy = (string?)null,
        activityCount = 1, triggerTypes = new[] { "manualTrigger" }, lastExecution = (object?)null,
        successCount = 0, totalCount = 0, avgDurationMs = (double?)null,
        checkedOutByUserId = (Guid?)null, checkedOutByUserName = (string?)null, checkedOutAt = (DateTime?)null,
    };

    private void StubGet(TestApi api, Guid id, string definitionJson) =>
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Wf(id, "WF", definitionJson)));

    [Fact]
    public void ValidateWorkflowDefinition_FlagsUnknownNodeAndUnresolvedEdge()
    {
        using var api = new TestApi();
        var tools = new WorkflowEditTools(api.Client());

        var ok = JsonSerializer.Serialize(tools.ValidateWorkflowDefinition(
            Json("""{"nodes":[{"id":"n1","type":"activity","data":{"activityType":"runScript"}}],"edges":[]}""")));
        ok.Should().Contain("\"isValid\":true");

        var bad = JsonSerializer.Serialize(tools.ValidateWorkflowDefinition(
            Json("""{"nodes":[{"id":"n1","type":"activity","data":{"activityType":"runScript"}}],"edges":[{"id":"e1","source":"n1","target":"ghost"}]}""")));
        bad.Should().Contain("\"isValid\":false");
        bad.Should().Contain("ghost");
    }

    [Fact]
    public async Task PublishWorkflow_RestoresRealSecret_NotTheAgentsMask()
    {
        var id = Guid.NewGuid();
        // Stored definition has a REAL webhook secret.
        var current = """{"nodes":[{"id":"n1","type":"activity","data":{"activityType":"webhookTrigger","config":{"secret":"real-secret-value"}}}],"edges":[]}""";
        using var api = new TestApi();
        StubGet(api, id, current);
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/publish").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Wf(id, "WF", current)));

        // The agent only saw the REDACTED def, so it sends "***" for the secret.
        var proposed = """{"nodes":[{"id":"n1","type":"activity","data":{"activityType":"webhookTrigger","config":{"secret":"***"}}}],"edges":[]}""";

        var tools = new WorkflowEditTools(api.Client());
        await tools.PublishWorkflow(id.ToString(), Json(proposed));

        var body = api.Server.LogEntries.Last(e => e.RequestMessage.Path.EndsWith("/publish")).RequestMessage.Body ?? "";
        body.Should().Contain("real-secret-value");   // restored from the current version
        body.Should().NotContain("\"***\"");            // the mask was NOT written over the real secret
    }

    [Fact]
    public async Task ApplyWorkflowPatch_PreservesUntouchedNodeAndSecret_AndPublishes()
    {
        var id = Guid.NewGuid();
        var current = """
        {"nodes":[
          {"id":"n1","type":"activity","data":{"activityType":"runScript","config":{"script":"old"}}},
          {"id":"n2","type":"activity","data":{"activityType":"restApi","config":{"apiKey":"real-key","url":"https://x"}}}
        ],"edges":[]}
        """;
        using var api = new TestApi();
        StubGet(api, id, current);
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/publish").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Wf(id, "WF", current)));

        // Only touch n1's script. n2 (with its real apiKey) must survive untouched.
        var ops = """[{"op":"upsertNode","node":{"id":"n1","type":"activity","data":{"activityType":"runScript","config":{"script":"new"}}}}]""";

        var tools = new WorkflowEditTools(api.Client());
        await tools.ApplyWorkflowPatch(id.ToString(), Json(ops), publish: true);

        var body = api.Server.LogEntries.Last(e => e.RequestMessage.Path.EndsWith("/publish")).RequestMessage.Body ?? "";
        body.Should().Contain("new");          // n1 edit applied
        body.Should().Contain("real-key");      // n2 secret preserved on the wire
        body.Should().Contain("https://x");     // n2 untouched
    }

    [Fact]
    public async Task ApplyWorkflowPatch_InvalidResult_IsNotPersisted()
    {
        var id = Guid.NewGuid();
        var current = """{"nodes":[{"id":"n1","type":"activity","data":{"activityType":"runScript"}}],"edges":[]}""";
        using var api = new TestApi();
        StubGet(api, id, current);
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/publish").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Wf(id, "WF", current)));

        // Edge to a non-existent node → structurally invalid.
        var ops = """[{"op":"upsertEdge","edge":{"id":"e1","source":"n1","target":"ghost"}}]""";
        var tools = new WorkflowEditTools(api.Client());

        var act = async () => await tools.ApplyWorkflowPatch(id.ToString(), Json(ops), publish: true);
        await act.Should().ThrowAsync<Exception>();

        // Nothing was published.
        api.Server.LogEntries.Any(e => e.RequestMessage.Path.EndsWith("/publish")).Should().BeFalse();
    }

    [Fact]
    public async Task PublishWorkflow_RejectsMalformedShape_WithoutSaving()
    {
        var id = Guid.NewGuid();
        var current = """{"nodes":[{"id":"n1","type":"activity","data":{"activityType":"runScript"}}],"edges":[]}""";
        using var api = new TestApi();
        StubGet(api, id, current);
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/publish").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Wf(id, "WF", current)));

        var tools = new WorkflowEditTools(api.Client());
        // A malformed call: missing nodes/edges arrays. Must be refused, not persisted as empty.
        var act = async () => await tools.PublishWorkflow(id.ToString(), Json("{}"));
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("nodes");

        api.Server.LogEntries.Any(e => e.RequestMessage.Path.EndsWith("/publish")).Should().BeFalse();
    }

    [Fact]
    public async Task ApplyWorkflowPatch_DryRun_ReturnsValidationEvenWhenInvalid_AndDoesNotSave()
    {
        var id = Guid.NewGuid();
        var current = """{"nodes":[{"id":"n1","type":"activity","data":{"activityType":"runScript"}}],"edges":[]}""";
        using var api = new TestApi();
        StubGet(api, id, current);
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/publish").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Wf(id, "WF", current)));

        // Invalid result (edge to ghost), but dryRun must NOT throw — it returns the validation.
        var ops = """[{"op":"upsertEdge","edge":{"id":"e1","source":"n1","target":"ghost"}}]""";
        var tools = new WorkflowEditTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.ApplyWorkflowPatch(id.ToString(), Json(ops), dryRun: true, publish: true));

        json.Should().Contain("\"dryRun\":true");
        json.Should().Contain("\"isValid\":false");
        json.Should().Contain("ghost");
        api.Server.LogEntries.Any(e => e.RequestMessage.Path.EndsWith("/publish")).Should().BeFalse();
    }

    [Fact]
    public async Task LockWorkflow_Conflict_SurfacesActionableError()
    {
        var id = Guid.NewGuid();
        var current = """{"nodes":[],"edges":[]}""";
        using var api = new TestApi();
        StubGet(api, id, current);
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/lock").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(409).WithBodyAsJson(new { title = "Conflict", detail = "Already checked out" }));

        var tools = new WorkflowEditTools(api.Client());
        var act = async () => await tools.LockWorkflow(id.ToString());
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("Conflict");
    }
}
