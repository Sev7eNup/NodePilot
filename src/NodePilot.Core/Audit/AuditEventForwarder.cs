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

        var outcome = MapEventOutcome(entry.Action);
        var supportLog = SupportLogActions.Contains(entry.Action) || outcome == "failure";
        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["support.event_type"] = "AUDIT",
            ["support.message"] = $"{entry.Action} user={entry.Username ?? "-"} resource={entry.ResourceType ?? "-"}/{entry.ResourceId?.ToString() ?? "-"} ip={entry.IpAddress ?? "-"}",
            ["event.action"] = entry.Action,
            ["event.category"] = MapEventCategory(entry.Action),
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

    private static string MapEventOutcome(string action)
    {
        if (action == "LOGIN_LOCKED"
            || action.EndsWith("_FAILED", StringComparison.Ordinal)
            || action.EndsWith("_SUPPRESSED", StringComparison.Ordinal)
            || action.EndsWith("_REJECTED", StringComparison.Ordinal)
            || action.EndsWith("_REFUSED_COLLISION", StringComparison.Ordinal)
            || action.EndsWith("_REFUSED_LAST_ADMIN", StringComparison.Ordinal))
            return "failure";
        return "success";
    }

    private static string MapEventCategory(string action)
    {
        if (action.StartsWith("LOGIN_", StringComparison.Ordinal)
            || action == "LOGOUT"
            || action.StartsWith("TOKEN_", StringComparison.Ordinal)
            || action.StartsWith("USER_", StringComparison.Ordinal))
            return "iam";
        if (action.StartsWith("CREDENTIAL_", StringComparison.Ordinal)
            || action.StartsWith("GLOBAL_VARIABLE_", StringComparison.Ordinal)
            || action.StartsWith("FOLDER_PERMISSION_", StringComparison.Ordinal)
            || action == "SECRETS_REENCRYPTED")
            return "iam";
        if (action.StartsWith("EXECUTION_", StringComparison.Ordinal)
            || action == "WEBHOOK_TRIGGERED"
            || action == "EXTERNAL_TRIGGER_FIRED"
            || action == "TRIGGER_FIRE_SUPPRESSED")
            return "process";
        return "configuration";
    }
}
