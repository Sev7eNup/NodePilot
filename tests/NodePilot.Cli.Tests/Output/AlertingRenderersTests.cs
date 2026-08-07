using FluentAssertions;
using NodePilot.Cli.Api.Dtos;
using NodePilot.Cli.Output;
using Spectre.Console.Testing;
using Xunit;

namespace NodePilot.Cli.Tests.Output;

/// <summary>
/// The five table renderers that no `-o table` command test reached: alerting rules and
/// deliveries, system-alert policies and sources, and global-variable folders. Each asserts
/// the condensed cells the renderer computes — the truncated event list, the scope/target
/// summary, the route-channel roll-up — not just that a row appeared.
/// </summary>
public class AlertingRenderersTests
{
    private static TestConsole NewBuffer()
    {
        var console = new TestConsole();
        // 80 cols wraps mid-cell and makes Contain() assertions flaky.
        console.Profile.Width = 240;
        return console;
    }

    [Fact]
    public void AlertingRules_TruncatesTheEventListAfterTwoEntries()
    {
        var console = NewBuffer();

        Renderers.AlertingRules(console, [Rule(
            name: "Nightly failures",
            events: ["ExecutionFailed", "ExecutionCancelled", "ExecutionTimedOut", "StepFailed"])]);

        var output = console.Output;
        output.Should().Contain("Nightly failures");
        output.Should().Contain("ExecutionFailed,ExecutionCancelled,+2",
            "only the first two event types are listed, the rest is a count");
    }

    [Fact]
    public void AlertingRules_GlobalScopeIsRenderedWithoutATargetCount()
    {
        var console = NewBuffer();

        Renderers.AlertingRules(console, [Rule(scopeKind: "Global")]);

        console.Output.Should().Contain("Global").And.NotContain("Global (");
    }

    [Fact]
    public void AlertingRules_FolderScopeShowsTheTargetCount()
    {
        var console = NewBuffer();

        Renderers.AlertingRules(console, [Rule(
            scopeKind: "Folder",
            targets: [new NotificationRuleTargetDto("Folder", Guid.NewGuid()),
                      new NotificationRuleTargetDto("Folder", Guid.NewGuid())])]);

        console.Output.Should().Contain("Folder (2)");
    }

    [Fact]
    public void AlertingRules_RollsUpDuplicateRouteChannels()
    {
        var console = NewBuffer();

        Renderers.AlertingRules(console, [Rule(routes:
        [
            new NotificationRouteDto(Guid.NewGuid(), "email", "ops@example.test", null, 0),
            new NotificationRouteDto(Guid.NewGuid(), "email", "sre@example.test", null, 1),
            new NotificationRouteDto(Guid.NewGuid(), "webhook", "https://hooks.example.test", null, 2),
        ])]);

        console.Output.Should().Contain("email,webhook", "duplicate channels collapse to one entry");
    }

    [Fact]
    public void AlertingRules_WithoutRoutesOrCooldown_RendersPlaceholders()
    {
        var console = NewBuffer();

        Renderers.AlertingRules(console, [Rule(cooldownMinutes: 0, routes: [])]);

        console.Output.Should().Contain("-");
    }

    [Fact]
    public void AlertingRules_DisabledRuleIsMarkedNo()
    {
        var console = NewBuffer();

        Renderers.AlertingRules(console, [Rule(isEnabled: false)]);

        console.Output.Should().Contain("no");
    }

    [Fact]
    public void AlertingDeliveries_MarksTestFiresAndRendersChannelTargetPair()
    {
        var console = NewBuffer();

        Renderers.AlertingDeliveries(console, [Delivery(
            ruleName: "Nightly failures", channel: "email", target: "ops@example.test",
            status: "Sent", isTest: true)]);

        var output = console.Output;
        output.Should().Contain("Nightly failures");
        output.Should().Contain("[test]");
        output.Should().Contain("email:ops@example.test");
        output.Should().Contain("Sent");
    }

    [Fact]
    public void AlertingDeliveries_FailedDeliveryShowsAttemptAndError()
    {
        var console = NewBuffer();

        Renderers.AlertingDeliveries(console, [Delivery(
            status: "Failed", attempt: 3, error: "SMTP 554 rejected")]);

        var output = console.Output;
        output.Should().Contain("Failed");
        output.Should().Contain("3");
        output.Should().Contain("SMTP 554 rejected");
    }

    [Fact]
    public void AlertingDeliveries_UnknownStatusFallsBackToTheNeutralMarkup()
    {
        var console = NewBuffer();

        Renderers.AlertingDeliveries(console, [Delivery(status: "Pending")]);

        console.Output.Should().Contain("Pending");
    }

    [Fact]
    public void AlertingDeliveries_MissingRuleAndRouteRenderAsQuestionMarks()
    {
        var console = NewBuffer();

        Renderers.AlertingDeliveries(console, [Delivery(ruleName: null, channel: null, target: null)]);

        console.Output.Should().Contain("?");
    }

    [Fact]
    public void SystemAlertPolicies_RendersSourceAndPreset()
    {
        var console = NewBuffer();

        Renderers.SystemAlertPolicies(console, [Policy(
            name: "Queue depth", sourceId: "engine.queue-depth", presetId: "aggressive")]);

        var output = console.Output;
        output.Should().Contain("Queue depth");
        output.Should().Contain("engine.queue-depth");
        output.Should().Contain("aggressive");
    }

    [Fact]
    public void SystemAlertPolicies_FolderScopeShowsTargetCountAndCooldown()
    {
        var console = NewBuffer();

        Renderers.SystemAlertPolicies(console, [Policy(
            scopeKind: "Folder",
            targets: [new NotificationRuleTargetDto("Folder", Guid.NewGuid())],
            cooldownMinutes: 15)]);

        var output = console.Output;
        output.Should().Contain("Folder (1)");
        output.Should().Contain("15m");
    }

    [Fact]
    public void SystemAlertSources_RendersCountsAndAvailability()
    {
        var console = NewBuffer();

        Renderers.SystemAlertSources(console,
        [
            Source(sourceId: "engine.queue-depth", available: true,
                fields: 2, parameters: 1, presets: 3),
            Source(sourceId: "cluster.lease", available: false, defaultSeverity: null),
        ]);

        var output = console.Output;
        output.Should().Contain("engine.queue-depth");
        output.Should().Contain("cluster.lease");
        output.Should().Contain("yes");
        output.Should().Contain("no");
        output.Should().Contain("-", "a source without a default severity renders a placeholder");
    }

    [Fact]
    public void GlobalVariableFolders_AreSortedByPath()
    {
        var console = NewBuffer();

        Renderers.GlobalVariableFolders(console,
        [
            Folder(path: "/zeta", depth: 1, variableCount: 4),
            Folder(path: "/alpha", depth: 1, variableCount: 2),
        ]);

        var output = console.Output;
        output.IndexOf("/alpha", StringComparison.Ordinal)
            .Should().BeLessThan(output.IndexOf("/zeta", StringComparison.Ordinal));
        output.Should().Contain("4");
    }

    [Fact]
    public void GlobalVariableFolders_EmptyList_StillRendersTheHeader()
    {
        var console = NewBuffer();

        Renderers.GlobalVariableFolders(console, []);

        console.Output.Should().Contain("Path");
    }

    // ---------------------------------------------------------------- builders

    private static NotificationRuleResponse Rule(
        string name = "rule",
        bool isEnabled = true,
        List<string>? events = null,
        string scopeKind = "Global",
        int cooldownMinutes = 10,
        List<NotificationRouteDto>? routes = null,
        List<NotificationRuleTargetDto>? targets = null) => new(
        Guid.NewGuid(), name, null, isEnabled,
        events ?? ["ExecutionFailed"], null, scopeKind,
        cooldownMinutes, 1, 60,
        routes ?? [new NotificationRouteDto(Guid.NewGuid(), "email", "ops@example.test", null, 0)],
        targets ?? [],
        DateTime.UtcNow, DateTime.UtcNow, null);

    private static NotificationDeliveryDto Delivery(
        string? ruleName = "rule",
        string? channel = "email",
        string? target = "ops@example.test",
        string status = "Sent",
        int attempt = 1,
        string? error = null,
        bool isTest = false) => new(
        Guid.NewGuid(), Guid.NewGuid(), ruleName, Guid.NewGuid(), channel, target,
        "ExecutionFailed", status, attempt, DateTime.UtcNow, null,
        error, isTest, null);

    private static SystemAlertPolicyResponse Policy(
        string name = "policy",
        bool isEnabled = true,
        string sourceId = "engine.queue-depth",
        string? presetId = null,
        string scopeKind = "Global",
        List<NotificationRuleTargetDto>? targets = null,
        int cooldownMinutes = 10) => new(
        Guid.NewGuid(), name, null, isEnabled,
        sourceId, presetId, null,
        null, 60, null, scopeKind,
        targets ?? [],
        [new NotificationRouteDto(Guid.NewGuid(), "email", "ops@example.test", null, 0)],
        cooldownMinutes, 1, 60,
        DateTime.UtcNow, DateTime.UtcNow, null, null);

    private static SystemAlertSourceDto Source(
        string sourceId = "engine.queue-depth",
        string category = "Engine",
        string scopeCapability = "Global",
        string? defaultSeverity = "Warning",
        int fields = 1,
        int parameters = 1,
        int presets = 1,
        bool available = true) => new(
        sourceId, category, scopeCapability, defaultSeverity,
        [.. Enumerable.Range(0, fields).Select(i =>
            new SystemAlertFieldDto($"field{i}", "number", ["gt"], null, null))],
        [.. Enumerable.Range(0, parameters).Select(i =>
            new SystemAlertParameterDto($"param{i}", "number", null, false, null, null, null))],
        [.. Enumerable.Range(0, presets).Select(i =>
            new SystemAlertPresetDto($"preset{i}", "Warning", 60, null, null))],
        available);

    private static GlobalVariableFolderResponse Folder(
        string path = "/team", int depth = 1, int variableCount = 0) => new(
        Guid.NewGuid(), null, path.TrimStart('/'), path, depth,
        DateTime.UtcNow, null, variableCount);
}
