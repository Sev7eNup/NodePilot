using System.Text.Json;
using FluentAssertions;
using NodePilot.Mcp.Tests.Infra;
using NodePilot.Mcp.Tools;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Mcp.Tests;

public sealed class AlertingToolsTests
{
    private static object RuleJson(Guid id, string name) => new
    {
        id,
        name,
        description = (string?)null,
        isEnabled = true,
        eventTypes = new[] { "ExecutionFailed" },
        filterExpressionJson = (string?)null,
        scopeKind = "Global",
        cooldownMinutes = 10,
        minOccurrences = 1,
        occurrenceWindowMinutes = 0,
        routes = new object[] { new { id = Guid.NewGuid(), channel = "GenericWebhook", target = "https://hook", secret = "__unchanged__", order = 0 } },
        targets = Array.Empty<object>(),
        createdAt = DateTime.UtcNow,
        updatedAt = DateTime.UtcNow,
        updatedBy = "admin",
    };

    [Fact]
    public async Task ListAlertingRules_ReturnsSummaries_WithoutSecrets()
    {
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/alerting/rules").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[] { RuleJson(Guid.NewGuid(), "Prod-Fail") }));

        var tools = new AlertingTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.ListAlertingRules());
        json.Should().Contain("Prod-Fail").And.Contain("ExecutionFailed").And.Contain("https://hook");
        json.Should().NotContain("__unchanged__", "the route secret must never be surfaced");
    }

    [Fact]
    public async Task CreateAlertingRule_PostsRoutes_AndReturnsId()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/alerting/rules").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(RuleJson(id, "NewRule")));

        var tools = new AlertingTools(api.Client());
        var json = JsonSerializer.Serialize(
            await tools.CreateAlertingRule("NewRule", "ExecutionFailed,ExecutionCancelled", emails: "ops@x", webhooks: "https://hook"));

        json.Should().Contain(id.ToString()).And.Contain("\"created\":true");
        var body = api.Server.LogEntries.Last().RequestMessage.Body!;
        body.Should().Contain("ExecutionCancelled").And.Contain("GenericWebhook").And.Contain("https://hook");
    }

    [Fact]
    public async Task UpdateAlertingRule_Partial_PreservesUnsetFields()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/alerting/rules/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(RuleJson(id, "Original")));
        api.Server.Given(Request.Create().WithPath($"/api/alerting/rules/{id}").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(204));

        var tools = new AlertingTools(api.Client());
        await tools.UpdateAlertingRule(id.ToString(), name: "Renamed");

        var put = api.Server.LogEntries.Last(e => e.RequestMessage.Method == "PUT");
        put.RequestMessage.Body.Should().Contain("Renamed").And.Contain("\"cooldownMinutes\":10");
    }

    [Fact]
    public async Task ListAlertingDeliveries_ReturnsLedger_AndPassesFilters()
    {
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/alerting/deliveries").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
            {
                new
                {
                    id = Guid.NewGuid(), ruleId = Guid.NewGuid(), ruleName = "Prod-Fail",
                    routeId = Guid.NewGuid(), channel = "Email", target = "ops@x",
                    eventKey = "exec:abc:ExecutionFailed", status = "Failed", attempt = 2,
                    createdAt = DateTime.UtcNow, sentAt = DateTime.UtcNow, error = "smtp down",
                    isTest = false, summary = (string?)null,
                },
            }));

        var tools = new AlertingTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.ListAlertingDeliveries(status: "Failed"));
        json.Should().Contain("Prod-Fail").And.Contain("smtp down").And.Contain("\"count\":1");
        api.Server.LogEntries.Last().RequestMessage.AbsoluteUrl.Should().Contain("status=Failed");
    }

    [Fact]
    public async Task TestFireAlertingRule_ReturnsPerRouteResults()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/alerting/rules/{id}/test-fire").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                allSucceeded = false,
                results = new[] { new { channel = "Email", target = "ops@x", success = false, error = "smtp down" } },
            }));

        var tools = new AlertingTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.TestFireAlertingRule(id.ToString()));
        json.Should().Contain("\"allSucceeded\":false").And.Contain("smtp down");
    }

    [Fact]
    public async Task DeleteAlertingRule_IsDestructive_AndCallsDelete()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/alerting/rules/{id}").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(204));

        var tools = new DestructiveTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.DeleteAlertingRule(id.ToString()));
        json.Should().Contain("\"deleted\":true");
        api.Server.LogEntries.Should().Contain(e =>
            e.RequestMessage.AbsolutePath == $"/api/alerting/rules/{id}" && e.RequestMessage.Method == "DELETE");
    }
}
