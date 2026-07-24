using System.Text.Json;

namespace NodePilot.Core.Audit;

/// <summary>
/// Shared ECS classification for every audit forwarder. Keeping this in Core prevents
/// HTTP and background audit paths from assigning different outcomes to the same row.
/// </summary>
public static class AuditEventClassification
{
    public static string Outcome(string action, string? details = null)
    {
        if (action.EndsWith("_ATTEMPTED", StringComparison.Ordinal))
            return "unknown";

        if (DetailsReportFailure(details))
            return "failure";

        if (action == AuditActions.LoginLocked
            || action.EndsWith("_FAILED", StringComparison.Ordinal)
            || action.EndsWith("_SUPPRESSED", StringComparison.Ordinal)
            || action.EndsWith("_REJECTED", StringComparison.Ordinal)
            || action.EndsWith("_REFUSED_COLLISION", StringComparison.Ordinal)
            || action.EndsWith("_REFUSED_LAST_ADMIN", StringComparison.Ordinal))
        {
            return "failure";
        }

        return "success";
    }

    public static string Category(string action)
    {
        if (action.StartsWith("LOGIN_", StringComparison.Ordinal)
            || action == AuditActions.BreakGlassLoginSuccess
            || action == AuditActions.Logout
            || action.StartsWith("TOKEN_", StringComparison.Ordinal)
            || action.StartsWith("USER_", StringComparison.Ordinal))
        {
            return "iam";
        }

        if (action.StartsWith("CREDENTIAL_", StringComparison.Ordinal)
            || action.StartsWith("GLOBAL_VARIABLE_", StringComparison.Ordinal)
            || action.StartsWith("FOLDER_PERMISSION_", StringComparison.Ordinal)
            || action == AuditActions.SecretsReencrypted)
        {
            return "iam";
        }

        if (action.StartsWith("EXECUTION_", StringComparison.Ordinal)
            || action == AuditActions.WebhookTriggered
            || action == AuditActions.ExternalTriggerFired
            || action == AuditActions.TriggerFireSuppressed)
        {
            return "process";
        }

        return "configuration";
    }

    private static bool DetailsReportFailure(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
            return false;

        try
        {
            using var document = JsonDocument.Parse(details);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("success", out var success))
            {
                return false;
            }

            return success.ValueKind == JsonValueKind.False
                   || (success.ValueKind == JsonValueKind.String
                       && string.Equals(success.GetString(), "false", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
