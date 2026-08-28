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
    /// <summary>
    /// Allowlist of audit actions that are additionally mirrored into the support log, shared
    /// by the HTTP AuditWriter and every background or atomic forwarder call site. A failure
    /// outcome mirrors an entry as well, regardless of this list. Left out on purpose because
    /// they are high volume or routine: <c>CREDENTIAL_DECRYPTED</c>, <c>TOKEN_REFRESHED</c>,
    /// <c>WORKFLOW_CREATED/UPDATED/DUPLICATED/LOCKED/UNLOCKED/MOVED</c>, and <c>EXECUTION_*</c>,
    /// which the WorkflowEngine lifecycle helper reports separately.
    /// Membership references the <see cref="AuditActions"/> catalog, never a raw literal.
    /// </summary>
    private static readonly HashSet<string> SupportLogActions = new(StringComparer.Ordinal)
    {
        // Auth
        AuditActions.LoginSuccess, AuditActions.BreakGlassLoginSuccess,
        AuditActions.LoginFailed, AuditActions.LoginLocked, AuditActions.Logout,
        // User management
        AuditActions.UserCreated, AuditActions.UserCreatedBootstrap, AuditActions.UserDeleted,
        AuditActions.UserRoleChanged, AuditActions.UserBreakGlassChanged,
        AuditActions.UserPasswordReset, AuditActions.UserActivated, AuditActions.UserDeactivated,
        AuditActions.UserDirectoryAccessRefused, AuditActions.UserAuthorizationStale,
        AuditActions.UserScimProvisioned, AuditActions.UserScimUpdated, AuditActions.UserScimDeprovisioned,
        AuditActions.ScimGroupProvisioned, AuditActions.ScimGroupUpdated,
        AuditActions.ScimGroupDeprovisioned, AuditActions.ScimGroupReactivated,
        // Workflow lifecycle events
        AuditActions.WorkflowPublished, AuditActions.WorkflowDeleted, AuditActions.WorkflowForceUnlocked,
        // Trigger events
        AuditActions.ExternalTriggerFired, AuditActions.WebhookTriggered, AuditActions.TriggerFireSuppressed,
        // Secrets
        AuditActions.SecretsReencrypted,
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
