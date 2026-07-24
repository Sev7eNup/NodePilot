using Microsoft.Extensions.Logging;
using NodePilot.Core.Models;

namespace NodePilot.Core.Audit;

/// <summary>
/// Emits a persisted audit row as an ECS-shaped structured log event. Call only after
/// the DbContext save/transaction commits, so SIEM evidence never describes a mutation
/// that was rolled back. Shared by HTTP and background/atomic audit writers.
/// </summary>
public static class AuditEventForwarder
{
    private static readonly HashSet<string> SupportLogActions = new(StringComparer.Ordinal)
    {
        "LOGIN_SUCCESS", "LOGIN_FAILED", "LOGIN_LOCKED", "LOGOUT",
        "USER_CREATED", "USER_CREATED_BOOTSTRAP", "USER_DELETED", "USER_ROLE_CHANGED",
        "USER_PASSWORD_RESET", "USER_ACTIVATED", "USER_DEACTIVATED",
        "WORKFLOW_PUBLISHED", "WORKFLOW_DELETED", "WORKFLOW_FORCE_UNLOCKED",
        "EXTERNAL_TRIGGER_FIRED", "WEBHOOK_TRIGGERED", "TRIGGER_FIRE_SUPPRESSED",
        "SECRETS_REENCRYPTED",
    };

    public static void ForwardCommitted(ILogger? logger, AuditLogEntry entry)
    {
        if (logger is null) return;

        var outcome = AuditEventClassification.Outcome(entry.Action, entry.Details);
        var supportLog = SupportLogActions.Contains(entry.Action) || outcome == "failure";
        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["support.event_type"] = "AUDIT",
            ["support.message"] = $"{entry.Action} user={entry.Username ?? "-"} resource={entry.ResourceType ?? "-"}/{entry.ResourceId?.ToString() ?? "-"} ip={entry.IpAddress ?? "-"}",
            ["event.action"] = entry.Action,
            ["event.category"] = AuditEventClassification.Category(entry.Action),
            ["event.kind"] = "event",
            ["event.outcome"] = outcome,
            ["event.dataset"] = "nodepilot.audit",
            ["event.id"] = entry.Id.ToString(),
            ["event.original"] = entry.Details,
            ["user.id"] = entry.UserId?.ToString(),
            ["user.name"] = entry.Username,
            ["source.ip"] = entry.IpAddress,
            ["AuditResourceType"] = entry.ResourceType,
            ["AuditResourceId"] = entry.ResourceId?.ToString(),
            ["SupportLog"] = supportLog,
        }))
        {
            if (supportLog)
            {
                logger.LogInformation(
                    "AUDIT {Action} user={UserName} resource={ResourceType}/{ResourceId} ip={RemoteIp}",
                    entry.Action, entry.Username ?? "-", entry.ResourceType ?? "-",
                    entry.ResourceId?.ToString() ?? "-", entry.IpAddress ?? "-");
            }
            else
            {
                logger.LogInformation(
                    "audit.{Action} resource={ResourceType}/{ResourceId} actor={UserId} ip={RemoteIp}",
                    entry.Action, entry.ResourceType, entry.ResourceId, entry.UserId, entry.IpAddress);
            }
        }
    }

}
