using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using NodePilot.Mcp.Tests.Infra;
using NodePilot.Mcp.Tools;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Mcp.Tests.Tools;

/// <summary>Coverage for read + execution + edit tools that the focused tests did not
/// hit.</summary>
public sealed class MoreReadEditToolsTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string J(object o) => JsonSerializer.Serialize(o, Web);
    private static JsonElement E(string s) => JsonDocument.Parse(s).RootElement;

    // ---- Workflow read gaps -------------------------------------------------

    [Theory]
    [InlineData("manualTrigger", "parameters")]
    [InlineData("restApi", "url")]
    public async Task GetWorkflowNode_PreservesActivityAwareRedaction(string activityType, string opaqueKey)
    {
        var id = Guid.NewGuid();
        var definition = JsonSerializer.Serialize(new
        {
            nodes = new[] { new { id = "selected", data = new
            {
                activityType, label = "Selected step", config = new Dictionary<string, object>
                {
                    [opaqueKey] = activityType == "manualTrigger"
                        ? new[] { new { name = "token", type = "string", required = false, @default = "private-runtime-value" } }
                        : "https://service.example/api?code=private-runtime-value",
                    ["method"] = "GET",
                },
            } } },
            edges = Array.Empty<object>(),
        });
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(TestApi.WorkflowResponse(id, "WF", definition)));

        var serialized = J(await new CanvasAssistantTools(api.Client()).GetWorkflowNode(id.ToString(), "selected"));
        serialized.Should().NotContain("private-runtime-value");
        using var document = JsonDocument.Parse(serialized);
        var node = document.RootElement.GetProperty("node");
        node.GetProperty("id").GetString().Should().Be("selected");
        node.GetProperty("data").GetProperty("label").GetString().Should().Be("Selected step");
        var config = node.GetProperty("data").GetProperty("config");
        config.GetProperty(opaqueKey).GetString().Should().Be("***");
        config.GetProperty("method").GetString().Should().Be("GET");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetWorkflowContract_MasksLiteralDefaultsInToolOutput_PreservingDeclarations(bool byName)
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        var path = byName ? "/api/workflows/by-name/Provision/contract" : $"/api/workflows/{id}/contract";
        api.Server.Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                workflowId = id, workflowName = "Provision", hasManualTrigger = true, hasReturnData = true,
                hasMultipleReturnDataNodes = false,
                inputs = new[]
                {
                    new { name = "token", type = "string", required = false, @default = "private-deployment-token", description = "API token", hasConflict = false },
                    new { name = "optional", type = "string", required = false, @default = "", description = "Optional input", hasConflict = true },
                    new { name = "target", type = "string", required = true, @default = (string?)null, description = "Machine", hasConflict = false },
                },
                outputs = new[] { new { name = "result", source = "single" } },
            }));

        var serialized = J(await new WorkflowReadTools(api.Client())
            .GetWorkflowContract(byName ? "Provision" : id.ToString()));
        serialized.Should().NotContain("private-deployment-token");
        using var document = JsonDocument.Parse(serialized);
        var root = document.RootElement;
        root.GetProperty("workflowId").GetGuid().Should().Be(id);
        var inputs = root.GetProperty("inputs");
        inputs.GetArrayLength().Should().Be(3);
        inputs[0].GetProperty("default").GetString().Should().Be("***");
        inputs[0].GetProperty("name").GetString().Should().Be("token");
        inputs[0].GetProperty("type").GetString().Should().Be("string");
        inputs[0].GetProperty("description").GetString().Should().Be("API token");
        inputs[1].GetProperty("default").GetString().Should().Be("***");
        inputs[1].GetProperty("hasConflict").GetBoolean().Should().BeTrue();
        inputs[2].GetProperty("default").ValueKind.Should().Be(JsonValueKind.Null);
        inputs[2].GetProperty("required").GetBoolean().Should().BeTrue();
        root.GetProperty("outputs")[0].GetProperty("name").GetString().Should().Be("result");
    }

    [Fact]
    public async Task GetWorkflowContract_ById_ReturnsInputsOutputs()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/contract").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                workflowId = id, workflowName = "WF", hasManualTrigger = true, hasReturnData = true, hasMultipleReturnDataNodes = false,
                inputs = new[] { new { name = "env", type = "string", required = true, @default = (string?)null, description = (string?)null, hasConflict = false } },
                outputs = new[] { new { name = "result", source = "single" } },
            }));

        var json = J(await new WorkflowReadTools(api.Client()).GetWorkflowContract(id.ToString()));
        json.Should().Contain("env").And.Contain("result");
    }

    [Fact]
    public async Task ListWorkflowVersions_And_GetVersion_Work()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(TestApi.WorkflowResponse(id, "WF", "{}")));
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/versions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
            {
                new { version = 1, name = "WF", createdAt = DateTime.UtcNow, createdBy = "admin", changeNote = (string?)null, isCurrent = true },
            }));
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/versions/1").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                version = 1, name = "WF", description = (string?)null,
                definitionJson = "{\"nodes\":[{\"id\":\"n1\",\"type\":\"activity\",\"data\":{\"activityType\":\"runScript\",\"config\":{\"password\":\"sekret\"}}}],\"edges\":[]}",
                createdAt = DateTime.UtcNow, createdBy = "admin", changeNote = (string?)null, isCurrent = true,
            }));

        var tools = new WorkflowReadTools(api.Client());
        J(await tools.ListWorkflowVersions(id.ToString())).Should().Contain("\"version\":1");

        var ver = J(await tools.GetWorkflowVersion(id.ToString(), 1));
        ver.Should().Contain("n1");
        ver.Should().NotContain("sekret");   // version definition is redacted too
        ver.Should().Contain("***");
    }

    [Fact]
    public async Task ExportWorkflow_ReturnsEnvelope()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(TestApi.WorkflowResponse(id, "WF", "{}")));
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/export").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                schema = "nodepilot-workflow-export/v1", exportVersion = 1, exportedAt = DateTime.UtcNow,
                workflow = new { name = "WF", description = (string?)null, definition = new { nodes = Array.Empty<object>(), edges = Array.Empty<object>() }, isEnabled = (bool?)null },
                workflows = (object?)null,
            }));

        J(await new WorkflowReadTools(api.Client()).ExportWorkflow(id.ToString())).Should().Contain("nodepilot-workflow-export/v1");
    }

    // ---- Execution gaps -----------------------------------------------------

    [Fact]
    public async Task GetExecution_TruncatesReturnData()
    {
        var exec = Guid.NewGuid();
        var big = new string('r', 9000);
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/executions/{exec}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id = exec, workflowId = Guid.NewGuid(), status = "Succeeded", startedAt = DateTime.UtcNow,
                completedAt = (DateTime?)DateTime.UtcNow, triggeredBy = "manual", errorMessage = (string?)null, returnData = big,
            }));

        var json = J(await new ExecutionTools(api.Client()).GetExecution(exec.ToString()));
        json.Should().Contain("truncated");
        json.Length.Should().BeLessThan(big.Length);
    }

    [Fact]
    public async Task CancelAndRetryExecution_Work()
    {
        var exec = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/executions/{exec}/cancel").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));
        api.Server.Given(Request.Create().WithPath($"/api/executions/{exec}/retry").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(202).WithBodyAsJson(new
            {
                id = Guid.NewGuid(), workflowId = Guid.NewGuid(), status = "Pending", startedAt = DateTime.UtcNow,
                completedAt = (DateTime?)null, triggeredBy = "retry", errorMessage = (string?)null,
            }));

        var tools = new ExecutionTools(api.Client());
        J(await tools.CancelExecution(exec.ToString())).Should().Contain("\"cancelled\":true");
        J(await tools.RetryExecution(exec.ToString())).Should().Contain("Pending");
    }

    // ---- Edit gaps ----------------------------------------------------------

    [Fact]
    public async Task CreateWorkflow_MasksAgentSecret_AndRejectsMalformed()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/workflows").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(TestApi.WorkflowResponse(id, "New", "{}")));

        var tools = new WorkflowEditTools(api.Client());
        // A new node with a secret the agent invented -> must be masked, never sent as plaintext.
        var def = """{"nodes":[{"id":"h","type":"activity","data":{"activityType":"webhookTrigger","config":{"secret":"invented"}}}],"edges":[]}""";
        var res = J(await tools.CreateWorkflow("New", E(def)));
        res.Should().Contain("\"created\":true");

        var body = api.Server.LogEntries.Last(e => e.RequestMessage!.Method == "POST").RequestMessage!.Body ?? "";
        body.Should().NotContain("invented");
        body.Should().Contain("***");

        // Malformed shape -> rejected, no second POST.
        var act = async () => await tools.CreateWorkflow("Bad", E("{}"));
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task DuplicateEnableDisableUnlockRollback_Work()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(TestApi.WorkflowResponse(id, "WF", "{}")));
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/duplicate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(TestApi.WorkflowResponse(Guid.NewGuid(), "WF (copy)", "{}")));
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/enable").UsingPost()).RespondWith(Response.Create().WithStatusCode(204));
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/disable").UsingPost()).RespondWith(Response.Create().WithStatusCode(204));
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/unlock").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(TestApi.WorkflowResponse(id, "WF", "{}")));
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/rollback/2").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(TestApi.WorkflowResponse(id, "WF", "{}")));

        var tools = new WorkflowEditTools(api.Client());
        J(await tools.DuplicateWorkflow(id.ToString())).Should().Contain("\"duplicated\":true");
        J(await tools.EnableWorkflow(id.ToString())).Should().Contain("\"enabled\":true");
        J(await tools.DisableWorkflow(id.ToString())).Should().Contain("\"disabled\":true");
        J(await tools.UnlockWorkflow(id.ToString())).Should().Contain("\"unlocked\":true");
        J(await tools.RollbackWorkflow(id.ToString(), 2, "oops")).Should().Contain("\"rolledBack\":true");
    }

    [Fact]
    public async Task SetWorkflowConcurrencyLimit_Works()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(TestApi.WorkflowResponse(id, "WF", "{}")));
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/concurrency-limit").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(204));

        var tools = new WorkflowEditTools(api.Client());

        J(await tools.SetWorkflowConcurrencyLimit(id.ToString(), 5)).Should().Contain("\"maxConcurrentExecutions\":5");
        J(await tools.SetWorkflowConcurrencyLimit(id.ToString(), null)).Should().Contain("\"maxConcurrentExecutions\":null");
    }

    [Fact]
    public async Task ImportWorkflow_AndScorch_Work()
    {
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/workflows/import").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { created = 1, workflows = new[] { new { id = Guid.NewGuid(), name = "WF", originalName = "WF" } }, errors = Array.Empty<string>() }));
        api.Server.Given(Request.Create().WithPath("/api/workflows/import-scorch").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { created = 1, workflows = Array.Empty<object>(), variables = Array.Empty<object>(), warnings = new[] { "best-effort" }, errors = Array.Empty<string>() }));

        var tools = new WorkflowEditTools(api.Client());
        var env = """{"schema":"nodepilot-workflow-export/v1","exportVersion":1,"exportedAt":"2026-01-01T00:00:00Z","workflows":[{"name":"WF","definition":{"nodes":[],"edges":[]}}]}""";
        J(await tools.ImportWorkflow(E(env))).Should().Contain("\"created\":1");
        var xml = System.Text.Encoding.Unicode.GetPreamble()
            .Concat(System.Text.Encoding.Unicode.GetBytes("<runbook/>"))
            .ToArray();
        J(await tools.ImportScorchWorkflow(Convert.ToBase64String(xml))).Should().Contain("best-effort");
        var request = api.Server.LogEntries
            .Single(entry => entry.RequestMessage?.Path == "/api/workflows/import-scorch")
            .RequestMessage;
        request.Should().NotBeNull();
        request!.BodyAsBytes.Should().Equal(xml);
    }

    [Fact]
    public async Task ImportWorkflow_WithFolderId_AppendsQuery()
    {
        var folderId = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/workflows/import")
                .WithParam("folderId", folderId.ToString()).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { created = 1, workflows = Array.Empty<object>(), errors = Array.Empty<string>() }));
        api.Server.Given(Request.Create().WithPath("/api/workflows/import-scorch")
                .WithParam("folderId", folderId.ToString()).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { created = 1, workflows = Array.Empty<object>(), variables = Array.Empty<object>(), warnings = Array.Empty<string>(), errors = Array.Empty<string>() }));

        var tools = new WorkflowEditTools(api.Client());
        var env = """{"schema":"nodepilot-workflow-export/v1","exportVersion":1,"exportedAt":"2026-01-01T00:00:00Z","workflows":[{"name":"WF","definition":{"nodes":[],"edges":[]}}]}""";
        J(await tools.ImportWorkflow(E(env), folderId)).Should().Contain("\"created\":1");
        J(await tools.ImportScorchWorkflow(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("<runbook/>")), folderId))
            .Should().Contain("\"created\":1");
    }

    [Fact]
    public async Task PreviewWorkflowPatch_ReturnsRedactedMergeAndValidation_NoSave()
    {
        var id = Guid.NewGuid();
        var current = """{"nodes":[{"id":"n1","type":"activity","data":{"activityType":"webhookTrigger","config":{"secret":"real"}}}],"edges":[]}""";
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(TestApi.WorkflowResponse(id, "WF", current)));

        var ops = """[{"op":"upsertNode","node":{"id":"n2","type":"activity","data":{"activityType":"log","config":{"message":"hi"}}}}]""";
        var json = J(await new WorkflowEditTools(api.Client()).PreviewWorkflowPatch(id.ToString(), E(ops)));

        json.Should().Contain("n2");                 // new node in the merged preview
        json.Should().Contain("\"isValid\":true");
        json.Should().NotContain("real");            // existing secret redacted in the preview
        api.Server.LogEntries.Any(e => e.RequestMessage!.Method is "PUT" or "POST").Should().BeFalse();
    }

    [Fact]
    public async Task StepTestTools_Work()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(TestApi.WorkflowResponse(id, "WF", "{}")));
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/steps/n1/test").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                success = true, output = "ok", errorOutput = (string?)null,
                outputParameters = new Dictionary<string, string> { ["exitCode"] = "0" }, durationMs = 12.5, errorMessage = (string?)null,
            }));
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/steps/n1/test-context").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { executionId = (Guid?)null, executedAt = (DateTime?)null, status = (string?)null, variables = Array.Empty<object>() }));
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/steps/n1/test-context/runs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<object>()));

        var tools = new WorkflowEditTools(api.Client());
        J(await new DestructiveTools(api.Client()).TestStep(id.ToString(), "n1"))
            .Should().Contain("\"success\":true");
        J(await tools.GetStepTestContext(id.ToString(), "n1")).Should().Contain("variables");
        J(await tools.ListStepTestRuns(id.ToString(), "n1")).Should().Contain("runs");
    }
}
