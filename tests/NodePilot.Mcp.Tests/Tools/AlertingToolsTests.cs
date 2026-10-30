using System.Text.Json;
using FluentAssertions;
using NodePilot.Mcp.Tests.Infra;
using NodePilot.Mcp.Tools;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Mcp.Tests.Tools;

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
    public async Task GetAlertingCatalog_ReturnsTheRuleVocabulary()
    {
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/alerting/catalog").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                eventTypes = new object[] { new { name = "ExecutionFailed", category = "execution", scopeable = true } },
                eventFields = new object[] { new { name = "workflowName", applies = "execution", type = "string", values = (string[]?)null } },
                channels = new[] { "Email", "GenericWebhook" },
                dedupTemplateFields = new[] { "eventType" },
            }));

        var tools = new AlertingTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.GetAlertingCatalog());

        json.Should().Contain("ExecutionFailed").And.Contain("workflowName")
            .And.Contain("GenericWebhook").And.Contain("dedupTemplateFields");
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
        var body = api.Server.LogEntries.Last().RequestMessage!.Body!;
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

        var put = api.Server.LogEntries.Last(e => e.RequestMessage!.Method == "PUT");
        put.RequestMessage!.Body.Should().Contain("Renamed").And.Contain("\"cooldownMinutes\":10");
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
        api.Server.LogEntries.Last().RequestMessage!.AbsoluteUrl.Should().Contain("status=Failed");
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

    private const string SecretHook = "https://hook-user:PASS_SENTINEL@hooks.example.test:8443/services/PATH_SENTINEL?token=QUERY_SENTINEL";

    private static object RuleWithSecretHook(Guid id) => new
    {
        id, name = "Secret-Hook", description = (string?)null, isEnabled = true,
        eventTypes = new[] { "ExecutionFailed" }, filterExpressionJson = (string?)null, scopeKind = "Global",
        cooldownMinutes = 10, minOccurrences = 1, occurrenceWindowMinutes = 0,
        routes = new object[]
        {
            new { id = Guid.NewGuid(), channel = "GenericWebhook", target = SecretHook, secret = "__unchanged__", order = 0 },
            new { id = Guid.NewGuid(), channel = "Email", target = "ops@example.test", secret = (string?)null, order = 1 },
        },
        targets = Array.Empty<object>(), createdAt = DateTime.UtcNow, updatedAt = DateTime.UtcNow, updatedBy = "admin",
    };

    private static void AssertNoSentinel(string json)
    {
        json.Should().NotContain("PASS_SENTINEL").And.NotContain("PATH_SENTINEL").And.NotContain("QUERY_SENTINEL")
            .And.NotContain("hook-user");
    }

    [Fact]
    public async Task AlertingReadTools_RedactCredentialBearingWebhookTargets()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/alerting/rules").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[] { RuleWithSecretHook(id) }));
        api.Server.Given(Request.Create().WithPath($"/api/alerting/rules/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(RuleWithSecretHook(id)));
        api.Server.Given(Request.Create().WithPath("/api/alerting/deliveries").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
            {
                new
                {
                    id = Guid.NewGuid(), ruleId = id, ruleName = "Secret-Hook", routeId = Guid.NewGuid(),
                    channel = "GenericWebhook", target = SecretHook, eventKey = "exec:abc:ExecutionFailed",
                    status = "Failed", attempt = 1, createdAt = DateTime.UtcNow, sentAt = (DateTime?)null,
                    error = "Webhook returned HTTP 500.", isTest = false, summary = (string?)null,
                },
            }));
        api.Server.Given(Request.Create().WithPath($"/api/alerting/rules/{id}/test-fire").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                allSucceeded = true,
                results = new[] { new { channel = "GenericWebhook", target = SecretHook, success = true, error = (string?)null } },
            }));

        var tools = new AlertingTools(api.Client());
        var ct = TestContext.Current.CancellationToken;
        foreach (var json in new[]
        {
            JsonSerializer.Serialize(await tools.ListAlertingRules(ct)),
            JsonSerializer.Serialize(await tools.GetAlertingRule(id.ToString(), ct)),
            JsonSerializer.Serialize(await tools.ListAlertingDeliveries(cancellationToken: ct)),
            JsonSerializer.Serialize(await tools.TestFireAlertingRule(id.ToString(), ct)),
        })
        {
            AssertNoSentinel(json);
            json.Should().Contain("https://hooks.example.test:8443/***");
        }
        JsonSerializer.Serialize(await tools.GetAlertingRule(id.ToString(), ct)).Should().Contain("ops@example.test");
    }

    [Fact]
    public async Task UpdateAlertingRule_WithoutRoutes_KeepsServerSideWebhookTarget()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/alerting/rules/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(RuleWithSecretHook(id)));
        api.Server.Given(Request.Create().WithPath($"/api/alerting/rules/{id}").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(204));

        var tools = new AlertingTools(api.Client());
        var result = JsonSerializer.Serialize(await tools.UpdateAlertingRule(id.ToString(), name: "Renamed", cancellationToken: TestContext.Current.CancellationToken));

        AssertNoSentinel(result);
        var put = api.Server.LogEntries.Single(e => e.RequestMessage!.Method == "PUT").RequestMessage!.Body ?? "";
        put.Should().Contain("PATH_SENTINEL").And.Contain("QUERY_SENTINEL");
    }

    [Fact]
    public async Task UpdateAlertingRule_RedactedWebhookTarget_IsRejectedWithoutSaving()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/alerting/rules/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(RuleWithSecretHook(id)));

        var tools = new AlertingTools(api.Client());
        var act = () => tools.UpdateAlertingRule(id.ToString(), webhooks: "https://hooks.example.test:8443/***", cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<Exception>().WithMessage("*redacted target*");
        api.Server.LogEntries.Should().OnlyContain(e => e.RequestMessage!.Method == "GET");
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
            e.RequestMessage!.AbsolutePath == $"/api/alerting/rules/{id}" && e.RequestMessage!.Method == "DELETE");
    }
}
