using System.Text.Json;
using FluentAssertions;
using NodePilot.Mcp.Tests.Infra;
using NodePilot.Mcp.Tools;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Mcp.Tests;

public sealed class WorkflowReadToolsTests
{
    private static object WorkflowJson(Guid id, string name, bool enabled) => new
    {
        id,
        name,
        description = (string?)null,
        definitionJson = "{\"nodes\":[],\"edges\":[]}",
        version = 1,
        isEnabled = enabled,
        createdAt = DateTime.UtcNow,
        updatedAt = DateTime.UtcNow,
        createdBy = "admin",
        updatedBy = (string?)null,
        activityCount = 3,
        triggerTypes = new[] { "manualTrigger" },
        lastExecution = (object?)null,
        successCount = 0,
        totalCount = 0,
        avgDurationMs = (double?)null,
        checkedOutByUserId = (Guid?)null,
        checkedOutByUserName = (string?)null,
        checkedOutAt = (DateTime?)null,
    };

    [Fact]
    public async Task ListWorkflows_FiltersByNameAndEnabled_AndOmitsDefinition()
    {
        using var api = new TestApi();
        api.Server
            .Given(Request.Create().WithPath("/api/workflows").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
            {
                WorkflowJson(Guid.NewGuid(), "Disk Cleanup", enabled: true),
                WorkflowJson(Guid.NewGuid(), "Disk Report", enabled: false),
                WorkflowJson(Guid.NewGuid(), "Reboot Host", enabled: true),
            }));

        var tools = new WorkflowReadTools(api.Client());
        var result = await tools.ListWorkflows(nameContains: "disk", enabledOnly: true);

        var json = JsonSerializer.Serialize(result);
        json.Should().Contain("Disk Cleanup");
        json.Should().NotContain("Disk Report");   // filtered out: disabled
        json.Should().NotContain("Reboot Host");    // filtered out: name
        json.Should().NotContain("definitionJson");  // summaries omit the heavy field
        json.Should().Contain("\"count\":1");
    }

    [Fact]
    public async Task GetWorkflow_ById_ReturnsDetailWithoutDefinition()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        api.Server
            .Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(WorkflowJson(id, "Disk Cleanup", enabled: true)));

        var tools = new WorkflowReadTools(api.Client());
        var result = await tools.GetWorkflow(id.ToString());

        var json = JsonSerializer.Serialize(result);
        json.Should().Contain("Disk Cleanup");
        json.Should().Contain("\"enabled\":true");
        json.Should().NotContain("definitionJson");
    }

    [Fact]
    public async Task GetWorkflow_ByName_HitsByNameEndpoint()
    {
        using var api = new TestApi();
        // WireMock matches the decoded path; the client sends Uri.EscapeDataString("Disk Cleanup").
        api.Server
            .Given(Request.Create().WithPath("/api/workflows/by-name/Disk Cleanup").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(WorkflowJson(Guid.NewGuid(), "Disk Cleanup", enabled: true)));

        var tools = new WorkflowReadTools(api.Client());
        var result = await tools.GetWorkflow("Disk Cleanup");

        JsonSerializer.Serialize(result).Should().Contain("Disk Cleanup");
    }
}
