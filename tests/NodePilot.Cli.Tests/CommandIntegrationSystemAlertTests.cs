using System.Text.Json;
using FluentAssertions;
using NodePilot.Cli;
using NodePilot.Cli.Tests.Infra;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Cli.Tests;

/// <summary>
/// End-to-end command tests for <c>np system-alert ...</c> — the built-in infra/signal alert
/// policies introduced by ADR 0008 (replacing the old gauge-based alerting path). Covers
/// Spectre parsing, exit codes, the catalog/list/get read paths, enable/disable, the
/// non-interactive delete, and the create-from-file body translation.
/// </summary>
[Collection(CommandTestCollection.Name)]
public class CommandIntegrationSystemAlertTests
{
    private static object PolicyJson(Guid id, string name, string scope = "Global") => new
    {
        id,
        name,
        description = (string?)null,
        isEnabled = true,
        sourceId = "service.stale",
        presetId = (string?)null,
        sourceParameters = (object?)null,
        conditionJson = (string?)null,
        sustainForSeconds = 0,
        severityOverride = (string?)null,
        scopeKind = scope,
        targets = Array.Empty<object>(),
        routes = new object[] { new { id = Guid.NewGuid(), channel = "Email", target = "ops@x", secret = (string?)null, order = 0, conditionExpressionJson = (string?)null } },
        cooldownMinutes = 15,
        minOccurrences = 1,
        occurrenceWindowMinutes = 0,
        createdAt = DateTime.UtcNow,
        updatedAt = DateTime.UtcNow,
        updatedBy = "admin",
        activatedAt = (DateTime?)null,
    };

    [Fact]
    public void SystemAlertCatalog_RendersSources()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/alerting/system/catalog").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                sources = new object[]
                {
                    new
                    {
                        sourceId = "service.stale",
                        category = "Service",
                        scopeCapability = "GlobalOnly",
                        defaultSeverity = "Warning",
                        fields = new object[] { new { name = "signalValue", type = "number", operators = new[] { ">", "<" }, unit = "seconds", enumValues = (string[]?)null } },
                        parameters = new object[] { new { name = "threshold", type = "number", @default = 3, required = false, unit = "x", min = 1, max = 10 } },
                        presets = Array.Empty<object>(),
                        available = true,
                    },
                },
            }));

        var result = h.Run("system-alert", "catalog");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("service.stale");
    }

    [Fact]
    public void SystemAlertList_RendersPolicies()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/alerting/system/policies").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[] { PolicyJson(Guid.NewGuid(), "Stale-Service") }));

        var result = h.Run("system-alert", "list");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("Stale-Service");
    }

    [Fact]
    public void SystemAlertGet_RendersOnePolicy()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/alerting/system/policies/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(PolicyJson(id, "Stale-Service")));

        var result = h.Run("system-alert", "get", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("Stale-Service");
    }

    [Fact]
    public void SystemAlertEnable_PostsEnable()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/alerting/system/policies/{id}/enable").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = h.Run("system-alert", "enable", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
        h.Server.LogEntries.Should().Contain(e =>
            e.RequestMessage.AbsolutePath == $"/api/alerting/system/policies/{id}/enable" && e.RequestMessage.Method == "POST");
    }

    [Fact]
    public void SystemAlertDisable_PostsDisable()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/alerting/system/policies/{id}/disable").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = h.Run("system-alert", "disable", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
        h.Server.LogEntries.Should().Contain(e =>
            e.RequestMessage.AbsolutePath == $"/api/alerting/system/policies/{id}/disable" && e.RequestMessage.Method == "POST");
    }

    [Fact]
    public void SystemAlertDelete_NonInteractive_DeletesWithoutPrompt()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/alerting/system/policies/{id}").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = h.Run("system-alert", "delete", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
        h.Server.LogEntries.Should().Contain(e =>
            e.RequestMessage.AbsolutePath == $"/api/alerting/system/policies/{id}" && e.RequestMessage.Method == "DELETE");
    }

    [Fact]
    public void SystemAlertCreate_FromFile_PostsBody()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/alerting/system/policies").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(PolicyJson(Guid.NewGuid(), "Stale-Service")));

        var body = new
        {
            name = "Stale-Service",
            description = (string?)null,
            isEnabled = true,
            sourceId = "service.stale",
            presetId = (string?)null,
            sourceParameters = (object?)null,
            conditionJson = (string?)null,
            sustainForSeconds = 0,
            severityOverride = (string?)null,
            scopeKind = "Global",
            targets = Array.Empty<object>(),
            routes = new object[] { new { channel = "Email", target = "ops@x", order = 0 } },
            cooldownMinutes = 30,
            minOccurrences = 1,
            occurrenceWindowMinutes = 0,
        };
        var file = Path.Combine(h.ConfigDir, "policy.json");
        File.WriteAllText(file, JsonSerializer.Serialize(body));

        var result = h.Run("system-alert", "create", "--file", file);
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.StdErr.Should().Contain("Stale-Service");
        var sent = h.Server.LogEntries.Last().RequestMessage.Body!;
        sent.Should().Contain("service.stale");
        sent.Should().Contain("\"cooldownMinutes\":30");
    }

    [Fact]
    public void SystemAlertCreate_MissingFile_FailsBeforeFiring()
    {
        using var h = new CommandTestHarness();
        var result = h.Run("system-alert", "create", "--file", Path.Combine(h.ConfigDir, "does-not-exist.json"));
        result.ExitCode.Should().Be(ExitCodes.Error);
        result.StdErr.Should().Contain("nicht gefunden");
        h.Server.LogEntries.Should().NotContain(e =>
            e.RequestMessage.AbsolutePath == "/api/alerting/system/policies" && e.RequestMessage.Method == "POST");
    }

    [Fact]
    public void SystemAlertTestFire_FailedRoute_ReturnsErrorExit()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/alerting/system/policies/{id}/test-fire").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                allSucceeded = false,
                results = new[] { new { channel = "Email", target = "ops@x", success = false, error = "smtp down" } },
            }));

        var result = h.Run("system-alert", "test-fire", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Error);
        result.Output.Should().Contain("smtp down");
    }
}
