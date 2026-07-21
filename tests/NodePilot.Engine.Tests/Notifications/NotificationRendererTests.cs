using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Engine.Notifications;
using Xunit;

namespace NodePilot.Engine.Tests.Notifications;

public class NotificationRendererTests
{
    private static NotificationContext Sample(string? title = null) => new(
        EventType: NotificationEventType.ExecutionFailed,
        Severity: NotificationSeverity.Warning,
        EventKey: "exec:abc:ExecutionFailed",
        WorkflowId: Guid.NewGuid(),
        WorkflowName: "Nightly Backup",
        FolderId: null,
        FolderPath: "/ops",
        ExecutionId: Guid.NewGuid(),
        Status: "Failed",
        ErrorMessage: "disk full",
        DurationMs: 4200,
        OccurredAt: new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        TriggeredBy: "scheduler",
        CallDepth: 0,
        IsSubWorkflow: false,
        TargetMachine: null,
        SourceKey: null,
        Title: title,
        Summary: null,
        DeepLinkPath: "/executions/x");

    [Fact]
    public void Title_FallsBackToEventAndWorkflow_WhenNoExplicitTitle()
    {
        NotificationRenderer.Title(Sample()).Should().Be("[Warning] ExecutionFailed: Nightly Backup");
        NotificationRenderer.Title(Sample(title: "Custom")).Should().Be("Custom");
    }

    [Fact]
    public void EmailBody_IncludesRelevantFields_AndSkipsEmptyOnes()
    {
        var body = NotificationRenderer.EmailBody(Sample());
        body.Should().Contain("Workflow: Nightly Backup")
            .And.Contain("Status: Failed")
            .And.Contain("Error: disk full")
            .And.Contain("Duration: 4200 ms")
            .And.NotContain("Target machine:"); // null → line omitted
    }

    [Fact]
    public void WebhookJson_IsValidCamelCaseJson()
    {
        using var doc = JsonDocument.Parse(NotificationRenderer.WebhookJson(Sample()));
        var root = doc.RootElement;
        root.GetProperty("eventType").GetString().Should().Be("ExecutionFailed");
        root.GetProperty("severity").GetString().Should().Be("Warning");
        root.GetProperty("workflowName").GetString().Should().Be("Nightly Backup");
        root.GetProperty("status").GetString().Should().Be("Failed");
        root.GetProperty("durationMs").GetInt64().Should().Be(4200);
    }

    private static NotificationContext GaugeSample() => new(
        EventType: NotificationEventType.BacklogHigh,
        Severity: NotificationSeverity.Warning,
        EventKey: "gauge:backlog:BacklogHigh:123",
        WorkflowId: null,
        WorkflowName: null,
        FolderId: null,
        FolderPath: null,
        ExecutionId: null,
        Status: null,
        ErrorMessage: null,
        DurationMs: null,
        OccurredAt: new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        TriggeredBy: null,
        CallDepth: 0,
        IsSubWorkflow: false,
        TargetMachine: "WEB01",
        SourceKey: "backlog",
        Title: "Backlog high",
        Summary: "750 executions in flight",
        DeepLinkPath: "/executions",
        SignalValue: 750);

    [Fact]
    public void WebhookJson_ExposesGaugeFields_AsStructuredValues()
    {
        // Regression: gauge measurements must be first-class JSON fields, not just text in the summary.
        using var doc = JsonDocument.Parse(NotificationRenderer.WebhookJson(GaugeSample()));
        var root = doc.RootElement;
        root.GetProperty("eventType").GetString().Should().Be("BacklogHigh");
        root.GetProperty("sourceKey").GetString().Should().Be("backlog");
        root.GetProperty("signalValue").GetInt64().Should().Be(750);
        root.GetProperty("targetMachine").GetString().Should().Be("WEB01");
    }
}
