using NodePilot.Core.Enums;

namespace NodePilot.Core.Models;

/// <summary>
/// One delivery target of a <see cref="NotificationRule"/>. A rule may fan out to several routes
/// at once (e.g. e-mail + a Teams channel). The <see cref="Target"/> meaning is channel-specific.
/// </summary>
public class NotificationRoute
{
    public Guid Id { get; set; }
    public Guid NotificationRuleId { get; set; }

    public NotificationChannel Channel { get; set; }

    /// <summary>
    /// Channel-specific destination: e-mail recipient(s) (comma-separated), webhook URL,
    /// PagerDuty routing key, or Opsgenie API key. Non-secret (the URL/recipient is shown in the UI).
    /// </summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>
    /// Optional secret material (generic-webhook HMAC signing secret, etc.), encrypted at rest via
    /// <c>ISecretProtector</c> and redacted in API responses. Null when the channel needs no secret.
    /// </summary>
    public string? Secret { get; set; }

    /// <summary>
    /// Optional route-local condition AST over the same event fields as the parent rule filter.
    /// Null/empty means this route receives every occurrence matched by the rule.
    /// </summary>
    public string? ConditionExpressionJson { get; set; }

    /// <summary>Display/iteration order within the rule.</summary>
    public int Order { get; set; }

    public NotificationRule Rule { get; set; } = null!;
}
