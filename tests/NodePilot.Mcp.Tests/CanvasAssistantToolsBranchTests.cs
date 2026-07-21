using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using NodePilot.Mcp.Tests.Infra;
using NodePilot.Mcp.Tools;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Mcp.Tests;

/// <summary>
/// Fills the remaining branch gaps in <see cref="CanvasAssistantTools"/>: the group/not/malformed
/// arms of the edge-condition validator, the unknown-activity-type reply, and the
/// preview_template_resolution paths (bad executionId, workflow-by-name run-context load).
/// </summary>
public sealed class CanvasAssistantToolsBranchTests
{
    private static readonly JsonSerializerOptions Web =
        new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string J(object o) => JsonSerializer.Serialize(o, Web);
    private static JsonElement E(string s) => JsonDocument.Parse(s).RootElement;
    private static CanvasAssistantTools Tools(TestApi api) => new(api.Client());

    [Fact]
    public void ValidateEdgeCondition_ValidGroupAndNot_AreAccepted()
    {
        using var api = new TestApi();
        var expr = """
        {"type":"group","op":"AND","children":[
          {"type":"comparison","op":"=="},
          {"type":"not","child":{"type":"comparison","op":"isEmpty"}}]}
        """;

        var json = J(Tools(api).ValidateEdgeCondition(E(expr)));

        json.Should().Contain("\"isValid\":true");
    }

    [Fact]
    public void ValidateEdgeCondition_GroupWithoutChildren_AndBadOp_AreRejected()
    {
        using var api = new TestApi();
        var json = J(Tools(api).ValidateEdgeCondition(E("""{"type":"group","op":"XOR","children":[]}""")));

        json.Should().Contain("\"isValid\":false");
        json.Should().Contain("AND or OR");
        json.Should().Contain("non-empty array");
    }

    [Fact]
    public void ValidateEdgeCondition_NotWithoutChild_IsRejected()
    {
        using var api = new TestApi();
        var json = J(Tools(api).ValidateEdgeCondition(E("""{"type":"not"}""")));

        json.Should().Contain("\"isValid\":false");
        json.Should().Contain("not needs a child");
    }

    [Fact]
    public void ValidateEdgeCondition_NonObject_AndMissingType_AreRejected()
    {
        using var api = new TestApi();
        J(Tools(api).ValidateEdgeCondition(E("\"just-a-string\"")))
            .Should().Contain("condition must be an object");

        J(Tools(api).ValidateEdgeCondition(E("""{"op":"=="}""")))
            .Should().Contain("missing string 'type'");
    }

    [Fact]
    public void ValidateActivityConfig_UnknownType_ReportsKnownTypeFalse()
    {
        using var api = new TestApi();
        var json = J(Tools(api).ValidateActivityConfig("notARealActivity", E("""{}""")));

        json.Should().Contain("\"knownType\":false");
        json.Should().Contain("Unknown activity type 'notARealActivity'");
    }

    [Fact]
    public async Task PreviewTemplateResolution_BadExecutionId_Throws()
    {
        using var api = new TestApi();
        var act = async () => await Tools(api).PreviewTemplateResolution(
            "{{n1.output}}", mockVariables: null,
            workflowId: Guid.NewGuid().ToString(), stepId: "n1", executionId: "not-a-guid");

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("executionId must be a GUID");
    }

    [Fact]
    public async Task PreviewTemplateResolution_ResolvesFromRunContext_ByWorkflowNameAndExecutionId()
    {
        var wf = Guid.NewGuid();
        var exec = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/workflows/by-name/Nightly").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(TestApi.WorkflowResponse(wf, "Nightly", "{}")));
        api.Server.Given(Request.Create().WithPath($"/api/workflows/{wf}/steps/n1/test-context").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                executionId = (Guid?)exec, executedAt = (DateTime?)DateTime.UtcNow, status = "Succeeded",
                variables = new[]
                {
                    new { key = "n1.param.hostName", origin = "n1", source = "param", value = "WEB77" },
                },
            }));

        var json = J(await Tools(api).PreviewTemplateResolution(
            "host {{n1.param.hostName}}", mockVariables: null,
            workflowId: "Nightly", stepId: "n1", executionId: exec.ToString()));

        json.Should().Contain("WEB77");
        json.Should().Contain($"run {exec}");   // contextNote names the explicit run
    }
}
