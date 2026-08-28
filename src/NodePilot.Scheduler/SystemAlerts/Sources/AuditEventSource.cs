using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Scheduler.SystemAlerts.Sources;

/// <summary>
/// Event source: audit-log entries (failed logins, lockouts, break-glass sign-ins, role changes,
/// credential
/// deletions, force-unlocks, …), one observation per row. Makes every <see cref="AuditActions"/>
/// code
/// alertable through the shared policy pipeline without a per-code event type — a policy's
/// condition filters
/// on <c>action</c>, <c>outcome</c>, <c>category</c>, <c>username</c>, <c>ipAddress</c>,
/// <c>resourceType</c>
/// or the (write-side redacted) <c>details</c> JSON.
///
/// Like <see cref="ExecutionResultSource"/> it is a stateless lookback window: the source contract
/// is
/// read-only, so there is no persisted cursor — the evaluator's activation watermark and the
/// delivery
/// ledger's per-occurrence key keep a row from alerting twice. The <c>actions</c> parameter
/// pre-filters
/// server-side so non-matching rows never become observations, and <c>lookbackSeconds</c> is capped
/// at a
/// day so the scan stays bounded. There is deliberately no per-pass row cap: over a sliding window
/// an
/// oldest-first cap is not a load guard but a cliff — once rows arrive faster than the cap per
/// dispatcher
/// interval, a growing band of them ages past the prefix and out of the window without ever being
/// observed, and an event row is observable exactly once.
/// </summary>
public sealed class AuditEventSource : ISystemAlertSource
{
    private const int DefaultLookbackSeconds = 300;
    /// <summary>Upper bound of the lookback window: bounds one sample to a day of audit rows,
    /// whatever the policy says.</summary>
    public const int MaxLookbackSeconds = 86_400;
    private const int SummaryDetailsChars = 200;

    /// <summary>
    /// Housekeeping codes skipped when <c>actions</c> is empty: <c>CREDENTIAL_DECRYPTED</c> is
    /// written for every
    /// step that resolves a credential, <c>TOKEN_REFRESHED</c> for every session rotation — volume,
    /// not signal,
    /// and either would crowd the cap. Naming one explicitly in <c>actions</c> includes it.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultExcludedActions =
        [AuditActions.CredentialDecrypted, AuditActions.TokenRefreshed];

    public string SourceId => "audit-event";

    public SystemAlertSourceDescriptor Describe() => new(
        SourceId, SystemAlertCategory.Security, SystemAlertScopeCapability.GlobalOnly, NotificationSeverity.Warning,
        Fields:
        [
            // String rather than Enum: the codes live in AuditActions (guarded by
            // AuditActionsCatalogTests);
            // an EnumValues copy here would be a second list to forget. Text operators filter it
            // fine.
            SystemAlertField.Of("action", SystemAlertFieldType.String),
            SystemAlertField.Of("outcome", SystemAlertFieldType.Enum, enumValues: ["success", "failure", "unknown"]),
            SystemAlertField.Of("category", SystemAlertFieldType.Enum, enumValues: ["iam", "process", "configuration"]),
            SystemAlertField.Of("username", SystemAlertFieldType.String),
            SystemAlertField.Of("ipAddress", SystemAlertFieldType.String),
            SystemAlertField.Of("resourceType", SystemAlertFieldType.String),
            // The redacted details JSON as written — lets a policy match on what a code alone can't
            // say,
            // e.g. contains "\"source\":\"Ldap\"" or "\"breakGlass\":true".
            SystemAlertField.Of("details", SystemAlertFieldType.String),
        ],
        Parameters:
        [
            new SystemAlertParameter("lookbackSeconds", SystemAlertFieldType.Duration,
                Default: DefaultLookbackSeconds, Required: false, Unit: "seconds", Min: 1, Max: MaxLookbackSeconds),
            // Comma-separated AuditActions codes (case-insensitive). Empty = every code except
            // DefaultExcludedActions. Applied in the query, not the condition, so it bounds the
            // scan.
            new SystemAlertParameter("actions", SystemAlertFieldType.String, Default: null, Required: false),
        ],
        Presets:
        [
            // Presets ship a condition only, never an `actions` value: a pre-filter that silently
            // contradicts a later-edited condition would be a policy that looks right and never
            // fires.
            new SystemAlertPreset("failed-login", NotificationSeverity.Warning, SustainForSeconds: 0,
                ConditionJson: SystemAlertConditions.Compare("action", "==", AuditActions.LoginFailed)),
            new SystemAlertPreset("account-locked", NotificationSeverity.Critical, SustainForSeconds: 0,
                ConditionJson: SystemAlertConditions.Compare("action", "==", AuditActions.LoginLocked)),
            new SystemAlertPreset("break-glass-login", NotificationSeverity.Critical, SustainForSeconds: 0,
                ConditionJson: SystemAlertConditions.Compare("action", "==", AuditActions.BreakGlassLoginSuccess)),
            new SystemAlertPreset("privileged-change", NotificationSeverity.Warning, SustainForSeconds: 0,
                ConditionJson: SystemAlertConditions.AnyOf("action",
                    AuditActions.UserRoleChanged, AuditActions.UserBreakGlassChanged, AuditActions.UserPasswordReset,
                    AuditActions.UserDeactivated, AuditActions.UserDeleted, AuditActions.CredentialDeleted,
                    AuditActions.WorkflowForceUnlocked, AuditActions.BackupRestored,
                    AuditActions.SettingsAuthenticationUpdated)),
        ]);

    public Task<bool> IsAvailableAsync(NodePilotDbContext db, CancellationToken ct) => Task.FromResult(true);

    public async Task<IReadOnlyList<SystemAlertObservation>> ObserveAsync(
        NodePilotDbContext db, SystemAlertQuery query, CancellationToken ct)
    {
        var lookback = Math.Clamp(query.GetInt("lookbackSeconds", DefaultLookbackSeconds), 1, MaxLookbackSeconds);
        var cutoff = DateTime.UtcNow.AddSeconds(-lookback);
        var actions = ParseActions(query.GetString("actions"));

        IQueryable<AuditLogEntry> q = db.AuditLog.AsNoTracking().Where(a => a.Timestamp >= cutoff);
        q = actions.Count > 0
            ? q.Where(a => actions.Contains(a.Action))
            : q.Where(a => !DefaultExcludedActions.Contains(a.Action));

        var rows = await q
            .OrderBy(a => a.Timestamp).ThenBy(a => a.Id)
            .Select(a => new { a.Id, a.Timestamp, a.Username, a.Action, a.ResourceType, a.ResourceId, a.Details, a.IpAddress })
            .ToListAsync(ct);

        return rows.Select(a =>
        {
            var outcome = AuditEventClassification.Outcome(a.Action, a.Details);
            var category = AuditEventClassification.Category(a.Action);
            // For a sign-in event the account that matters is the one being signed into, which
            // lives in
            // Details.username; the actor column is whoever sent the request — empty for the usual
            // anonymous
            // LOGIN_FAILED, but an already-signed-in browser that fails a second login would put
            // *its* name
            // there and mislabel the alert. Every other code keeps the actor (USER_ROLE_CHANGED is
            // "by admin",
            // its Details.username is the changed account).
            var attempted = DetailsString(a.Details, "username");
            var username = (IsSignInAction(a.Action) ? attempted ?? a.Username : a.Username ?? attempted) ?? "";
            var who = username.Length > 0 ? username : "system";
            var resource = a.ResourceType is null ? ""
                : a.ResourceId is null ? a.ResourceType
                : $"{a.ResourceType} {a.ResourceId.Value:D}";
            var excerpt = Truncate(a.Details, SummaryDetailsChars);

            return new SystemAlertObservation(
                SourceId,
                // The row id, not the username: InstanceKey becomes sourceKey and part of the event
                // key that
                // travels in the X-NodePilot-Event-Key header of every outbound webhook.
                InstanceKey: a.Id.ToString("N"),
                SeveritySuggestion: outcome == "failure" ? NotificationSeverity.Warning : NotificationSeverity.Info,
                Title: $"Audit {a.Action}: {who}",
                Summary: $"{a.Action} by {who}"
                    + (string.IsNullOrEmpty(a.IpAddress) ? "" : $" from {a.IpAddress}")
                    + (resource.Length > 0 ? $" on {resource}" : "")
                    + $" at {a.Timestamp:u}"
                    + (excerpt.Length > 0 ? $" — {excerpt}" : ""),
                DeepLinkPath: "/audit",
                Fields: new Dictionary<string, object?>
                {
                    ["action"] = a.Action,
                    ["outcome"] = outcome,
                    ["category"] = category,
                    ["username"] = username,
                    ["ipAddress"] = a.IpAddress ?? "",
                    ["resourceType"] = a.ResourceType ?? "",
                    ["details"] = a.Details ?? "",
                },
                OccurredAt: a.Timestamp);
        }).ToList();
    }

    private static bool IsSignInAction(string action)
        => action.StartsWith("LOGIN_", StringComparison.Ordinal) || action == AuditActions.BreakGlassLoginSuccess;

    /// <summary>Splits the <c>actions</c> parameter into distinct, upper-cased codes; blanks are
    /// dropped.</summary>
    public static IReadOnlyList<string> ParseActions(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToUpperInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToList();

    private static string? DetailsString(string? details, string property)
    {
        if (string.IsNullOrWhiteSpace(details)) return null;
        try
        {
            using var doc = JsonDocument.Parse(details);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(property, out var v)
                && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= max ? text : text[..max] + "…";
    }
}
