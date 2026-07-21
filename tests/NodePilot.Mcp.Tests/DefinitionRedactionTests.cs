using System.Text.Json;
using FluentAssertions;
using NodePilot.Mcp.Tests.Infra;
using NodePilot.Mcp.Tools;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Mcp.Tests;

/// <summary>
/// The API hands an Admin/Operator service account the RAW DefinitionJson (it only redacts for
/// Viewer). The MCP server must mask secrets itself before they reach the agent.
/// </summary>
public sealed class DefinitionRedactionTests
{
    [Fact]
    public async Task GetWorkflowDefinition_MasksSecretConfigValues()
    {
        var id = Guid.NewGuid();
        // A definition with an inline webhook secret + an API key + a harmless script.
        var definition = """
        {
          "nodes": [
            { "id": "n1", "type": "activity", "data": { "activityType": "webhookTrigger",
              "config": { "secret": "super-secret-value", "method": "POST" } } },
            { "id": "n2", "type": "activity", "data": { "activityType": "restApi",
              "config": { "apiKey": "sk-live-123", "url": "https://example.com", "password": "hunter2" } } },
            { "id": "n3", "type": "activity", "data": { "activityType": "runScript",
              "config": { "script": "Get-PSDrive C" } } }
          ],
          "edges": []
        }
        """;

        using var api = new TestApi();
        api.Server
            .Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id, name = "Hook", description = (string?)null, definitionJson = definition,
                version = 1, isEnabled = true, createdAt = DateTime.UtcNow, updatedAt = DateTime.UtcNow,
                createdBy = "admin", updatedBy = (string?)null, activityCount = 3,
                triggerTypes = new[] { "webhookTrigger" }, lastExecution = (object?)null,
                successCount = 0, totalCount = 0, avgDurationMs = (double?)null,
                checkedOutByUserId = (Guid?)null, checkedOutByUserName = (string?)null, checkedOutAt = (DateTime?)null,
            }));

        var tools = new WorkflowReadTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.GetWorkflowDefinition(id.ToString()));

        // Secrets gone, masked.
        json.Should().NotContain("super-secret-value");
        json.Should().NotContain("sk-live-123");
        json.Should().NotContain("hunter2");
        json.Should().Contain("***");
        // Non-secret content preserved.
        json.Should().Contain("Get-PSDrive C");
        json.Should().Contain("https://example.com");
    }
}
