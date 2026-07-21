using FluentAssertions;
using NodePilot.Cli;
using NodePilot.Cli.Tests.Infra;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Cli.Tests;

/// <summary>
/// End-to-end command tests for <c>np alerting ...</c> — Spectre parsing, exit codes, and the
/// option-to-request translation (events/routes), the get/update read-modify path, the
/// non-interactive delete, and the test-fire exit code on a failing route.
/// </summary>
[Collection(CommandTestCollection.Name)]
public class CommandIntegrationAlertingTests
{
    private static object RuleJson(Guid id, string name, string scope = "Global") => new
    {
        id,
        name,
        description = (string?)null,
        isEnabled = true,
        eventTypes = new[] { "ExecutionFailed" },
        filterExpressionJson = (string?)null,
        scopeKind = scope,
        cooldownMinutes = 15,
        minOccurrences = 1,
        occurrenceWindowMinutes = 0,
        routes = new object[] { new { id = Guid.NewGuid(), channel = "Email", target = "ops@x", secret = (string?)null, order = 0 } },
        targets = Array.Empty<object>(),
        createdAt = DateTime.UtcNow,
        updatedAt = DateTime.UtcNow,
        updatedBy = "admin",
    };

    [Fact]
    public void AlertingList_RendersRules()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/alerting/rules").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[] { RuleJson(Guid.NewGuid(), "Prod-Fail") }));

        var result = h.Run("alerting", "list");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("Prod-Fail");
    }

    [Fact]
    public void AlertingCreate_TranslatesEventsAndRoutes_AndPostsBody()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/alerting/rules").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(RuleJson(Guid.NewGuid(), "Prod-Fail")));

        var result = h.Run("alerting", "create",
            "--name", "Prod-Fail", "--event-types", "ExecutionFailed,ExecutionCancelled",
            "--email", "ops@x", "--webhook", "https://hook", "--cooldown-minutes", "30");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.StdErr.Should().Contain("Prod-Fail");
        var body = h.Server.LogEntries.Last().RequestMessage.Body!;
        body.Should().Contain("ExecutionFailed").And.Contain("ExecutionCancelled");
        body.Should().Contain("GenericWebhook").And.Contain("https://hook");
        body.Should().Contain("\"cooldownMinutes\":30");
    }

    [Fact]
    public void AlertingCreate_MissingName_FailsBeforeFiring()
    {
        using var h = new CommandTestHarness();
        var result = h.Run("alerting", "create", "--event-types", "ExecutionFailed", "--email", "ops@x");
        result.ExitCode.Should().Be(ExitCodes.Error);
        result.StdErr.Should().Contain("--name");
        h.Server.LogEntries.Should().NotContain(e =>
            e.RequestMessage.AbsolutePath == "/api/alerting/rules" && e.RequestMessage.Method == "POST");
    }

    [Fact]
    public void AlertingCreate_NoRoutes_FailsBeforeFiring()
    {
        using var h = new CommandTestHarness();
        var result = h.Run("alerting", "create", "--name", "X", "--event-types", "ExecutionFailed");
        result.ExitCode.Should().Be(ExitCodes.Error);
        result.StdErr.Should().Contain("--email");
    }

    [Fact]
    public void AlertingUpdate_FetchesCurrentThenPuts_PreservingUnsetFields()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath("/api/alerting/rules").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[] { RuleJson(id, "Prod-Fail") }));
        h.Server.Given(Request.Create().WithPath($"/api/alerting/rules/{id}").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = h.Run("alerting", "update", id.ToString(), "--name", "Renamed");

        result.ExitCode.Should().Be(ExitCodes.Success);
        var put = h.Server.LogEntries.Last(e => e.RequestMessage.Method == "PUT");
        put.RequestMessage.Body.Should().Contain("Renamed");
        put.RequestMessage.Body.Should().Contain("\"cooldownMinutes\":15"); // carried over
    }

    [Fact]
    public void AlertingDelete_NonInteractive_DeletesWithoutPrompt()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/alerting/rules/{id}").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = h.Run("alerting", "delete", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
        h.Server.LogEntries.Should().Contain(e =>
            e.RequestMessage.AbsolutePath == $"/api/alerting/rules/{id}" && e.RequestMessage.Method == "DELETE");
    }

    [Fact]
    public void AlertingDeliveries_RendersLedger()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/alerting/deliveries").UsingGet())
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

        var result = h.Run("alerting", "deliveries", "--status", "Failed");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("Prod-Fail").And.Contain("smtp down");
        h.Server.LogEntries.Last().RequestMessage.AbsoluteUrl.Should().Contain("status=Failed");
    }

    [Fact]
    public void AlertingTestFire_FailedRoute_ReturnsErrorExit()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/alerting/rules/{id}/test-fire").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                allSucceeded = false,
                results = new[] { new { channel = "Email", target = "ops@x", success = false, error = "smtp down" } },
            }));

        var result = h.Run("alerting", "test-fire", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Error);
        result.Output.Should().Contain("smtp down");
    }
}
