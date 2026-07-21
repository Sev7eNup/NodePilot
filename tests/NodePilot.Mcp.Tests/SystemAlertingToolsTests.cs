using System.Text.Json;
using FluentAssertions;
using NodePilot.Mcp.Tests.Infra;
using NodePilot.Mcp.Tools;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Mcp.Tests;

public sealed class SystemAlertingToolsTests
{
    private static object PolicyJson(Guid id, string name) => new
    {
        id,
        name,
        description = (string?)null,
        isEnabled = true,
        sourceId = "backlog",
        presetId = (string?)null,
        sourceParameters = new Dictionary<string, object?> { ["threshold"] = 500 },
        conditionJson = (string?)null,
        sustainForSeconds = 60,
        severityOverride = (string?)null,
        scopeKind = "Global",
        targets = Array.Empty<object>(),
        routes = new object[] { new { id = Guid.NewGuid(), channel = "GenericWebhook", target = "https://hook", secret = "__unchanged__", order = 0, conditionExpressionJson = (string?)null } },
        cooldownMinutes = 10,
        minOccurrences = 1,
        occurrenceWindowMinutes = 0,
        createdAt = DateTime.UtcNow,
        updatedAt = DateTime.UtcNow,
        updatedBy = "admin",
        activatedAt = (DateTime?)DateTime.UtcNow,
    };

    [Fact]
    public async Task GetSystemAlertCatalog_ReturnsSources()
    {
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/alerting/system/catalog").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                sources = new[]
                {
                    new
                    {
                        sourceId = "backlog", category = "Queue", scopeCapability = "GlobalOnly",
                        defaultSeverity = "Warning",
                        fields = new[] { new { name = "pending", type = "Number", operators = new[] { ">", ">=" }, unit = "count", enumValues = (string[]?)null } },
                        parameters = new[] { new { name = "threshold", type = "Number", @default = (object?)500, required = false, unit = "count", min = (double?)0, max = (double?)null } },
                        presets = new[] { new { presetId = "default", severity = "Warning", sustainForSeconds = 60, conditionJson = (string?)null, parameters = (object?)null } },
                        available = true,
                    },
                },
            }));

        var tools = new SystemAlertingTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.GetSystemAlertCatalog());
        json.Should().Contain("backlog").And.Contain("GlobalOnly").And.Contain("\"count\":1").And.Contain("pending");
    }

    [Fact]
    public async Task ListSystemAlertPolicies_ReturnsSummaries_WithoutSecrets()
    {
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/alerting/system/policies").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[] { PolicyJson(Guid.NewGuid(), "Backlog-High") }));

        var tools = new SystemAlertingTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.ListSystemAlertPolicies());
        json.Should().Contain("Backlog-High").And.Contain("backlog").And.Contain("https://hook");
        json.Should().NotContain("__unchanged__", "the route secret must never be surfaced");
    }

    [Fact]
    public async Task GetSystemAlertPolicy_ReturnsOne_WithoutSecret()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/alerting/system/policies/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(PolicyJson(id, "Backlog-High")));

        var tools = new SystemAlertingTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.GetSystemAlertPolicy(id.ToString()));
        json.Should().Contain("Backlog-High").And.Contain(id.ToString());
        json.Should().NotContain("__unchanged__");
    }

    [Fact]
    public async Task CreateSystemAlertPolicy_PostsSourceAndRoutes_AndReturnsId()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/alerting/system/policies").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(PolicyJson(id, "NewPolicy")));

        var tools = new SystemAlertingTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.CreateSystemAlertPolicy(
            "NewPolicy", "backlog", sourceParametersJson: "{\"threshold\":500}",
            sustainForSeconds: 60, emails: "ops@x", webhooks: "https://hook"));

        json.Should().Contain(id.ToString()).And.Contain("\"created\":true");
        var body = api.Server.LogEntries.Last().RequestMessage.Body!;
        body.Should().Contain("backlog").And.Contain("threshold").And.Contain("GenericWebhook").And.Contain("https://hook");
    }

    [Fact]
    public async Task UpdateSystemAlertPolicy_Partial_PreservesUnsetFields()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/alerting/system/policies/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(PolicyJson(id, "Original")));
        api.Server.Given(Request.Create().WithPath($"/api/alerting/system/policies/{id}").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(204));

        var tools = new SystemAlertingTools(api.Client());
        await tools.UpdateSystemAlertPolicy(id.ToString(), name: "Renamed");

        var put = api.Server.LogEntries.Last(e => e.RequestMessage.Method == "PUT");
        put.RequestMessage.Body.Should().Contain("Renamed").And.Contain("\"cooldownMinutes\":10").And.Contain("backlog");
    }

    [Fact]
    public async Task EnableSystemAlertPolicy_PostsEnable()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/alerting/system/policies/{id}/enable").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));

        var tools = new SystemAlertingTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.EnableSystemAlertPolicy(id.ToString()));
        json.Should().Contain("\"enabled\":true");
        api.Server.LogEntries.Should().Contain(e =>
            e.RequestMessage.AbsolutePath == $"/api/alerting/system/policies/{id}/enable" && e.RequestMessage.Method == "POST");
    }

    [Fact]
    public async Task DisableSystemAlertPolicy_PostsDisable()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/alerting/system/policies/{id}/disable").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));

        var tools = new SystemAlertingTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.DisableSystemAlertPolicy(id.ToString()));
        json.Should().Contain("\"disabled\":true");
        api.Server.LogEntries.Should().Contain(e =>
            e.RequestMessage.AbsolutePath == $"/api/alerting/system/policies/{id}/disable" && e.RequestMessage.Method == "POST");
    }

    [Fact]
    public async Task TestFireSystemAlertPolicy_ReturnsPerRouteResults()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/alerting/system/policies/{id}/test-fire").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                allSucceeded = false,
                results = new[] { new { channel = "Email", target = "ops@x", success = false, error = "smtp down" } },
            }));

        var tools = new SystemAlertingTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.TestFireSystemAlertPolicy(id.ToString()));
        json.Should().Contain("\"allSucceeded\":false").And.Contain("smtp down");
    }

    [Fact]
    public async Task DeleteSystemAlertPolicy_IsDestructive_AndCallsDelete()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/alerting/system/policies/{id}").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(204));

        var tools = new DestructiveTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.DeleteSystemAlertPolicy(id.ToString()));
        json.Should().Contain("\"deleted\":true");
        api.Server.LogEntries.Should().Contain(e =>
            e.RequestMessage.AbsolutePath == $"/api/alerting/system/policies/{id}" && e.RequestMessage.Method == "DELETE");
    }
}
