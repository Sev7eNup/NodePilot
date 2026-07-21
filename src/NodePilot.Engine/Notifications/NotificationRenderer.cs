using System.Text;
using System.Text.Json;
using NodePilot.Core.Models;

namespace NodePilot.Engine.Notifications;

/// <summary>
/// Pure rendering of a <see cref="NotificationContext"/> into channel-specific payloads. Kept
/// separate from the sinks so payload shape is unit-testable without SMTP/HTTP. Teams/Slack/
/// PagerDuty/Opsgenie renderers are expected to land in a later alerting release alongside
/// their sinks.
/// </summary>
public static class NotificationRenderer
{
    public static string Title(NotificationContext c)
        => c.Title ?? $"[{c.Severity}] {c.EventType}" + (string.IsNullOrEmpty(c.WorkflowName) ? "" : $": {c.WorkflowName}");

    /// <summary>Plain-text e-mail body — a short summary line followed by the relevant fields.</summary>
    public static string EmailBody(NotificationContext c)
    {
        var sb = new StringBuilder();
        sb.AppendLine(c.Summary ?? Title(c));
        sb.AppendLine();
        void Line(string label, string? value)
        {
            if (!string.IsNullOrEmpty(value)) sb.AppendLine($"{label}: {value}");
        }
        Line("Event", c.EventType.ToString());
        Line("Severity", c.Severity.ToString());
        Line("Workflow", c.WorkflowName);
        Line("Folder", c.FolderPath);
        Line("Status", c.Status);
        if (c.DurationMs is { } d) Line("Duration", $"{d} ms");
        Line("Target machine", c.TargetMachine);
        Line("Triggered by", c.TriggeredBy);
        Line("Error", c.ErrorMessage);
        Line("When", c.OccurredAt.ToString("u"));
        return sb.ToString();
    }

    /// <summary>Generic-webhook JSON body (camelCase). Also the payload the HMAC signature covers.</summary>
    public static string WebhookJson(NotificationContext c)
        => JsonSerializer.Serialize(new
        {
            eventType = c.EventType.ToString(),
            severity = c.Severity.ToString(),
            title = Title(c),
            summary = c.Summary,
            workflowId = c.WorkflowId,
            workflowName = c.WorkflowName,
            folderPath = c.FolderPath,
            executionId = c.ExecutionId,
            status = c.Status,
            errorMessage = c.ErrorMessage,
            durationMs = c.DurationMs,
            targetMachine = c.TargetMachine,
            triggeredBy = c.TriggeredBy,
            // Gauge-event measurements as first-class fields (not just baked into the summary text) so
            // webhook consumers can branch on them. Null for execution events.
            sourceKey = c.SourceKey,
            signalValue = c.SignalValue,
            occurredAt = c.OccurredAt,
            deepLinkPath = c.DeepLinkPath,
        });
}
