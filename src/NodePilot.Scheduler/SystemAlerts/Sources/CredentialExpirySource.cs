using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Scheduler.SystemAlerts.Sources;

/// <summary>
/// Per-credential source: one observation per credential that has an <c>ExpiresAt</c>, keyed by credential id.
/// Exposes <c>daysLeft</c> (negative once expired) and <c>expired</c> — the policy picks the warn window
/// (e.g. <c>daysLeft &lt;= 14</c>). Available only when at least one credential tracks expiry.
/// </summary>
public sealed class CredentialExpirySource : ISystemAlertSource
{
    public string SourceId => "credential-expiring";

    public SystemAlertSourceDescriptor Describe() => new(
        SourceId, SystemAlertCategory.Credential, SystemAlertScopeCapability.GlobalOnly, NotificationSeverity.Warning,
        Fields:
        [
            SystemAlertField.Of("daysLeft", SystemAlertFieldType.Number, unit: "days"),
            SystemAlertField.Of("expired", SystemAlertFieldType.Boolean),
        ],
        Parameters: [],
        Presets: [new SystemAlertPreset("expiring-soon", NotificationSeverity.Warning, 0, SystemAlertConditions.Compare("daysLeft", "<=", "14"))]);

    public async Task<bool> IsAvailableAsync(NodePilotDbContext db, CancellationToken ct)
        => await db.Credentials.AsNoTracking().AnyAsync(c => c.ExpiresAt != null, ct);

    public async Task<IReadOnlyList<SystemAlertObservation>> ObserveAsync(NodePilotDbContext db, SystemAlertQuery query, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var creds = await db.Credentials.AsNoTracking()
            .Where(c => c.ExpiresAt != null)
            .Select(c => new { c.Id, c.Name, ExpiresAt = c.ExpiresAt!.Value })
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        return creds.Select(c =>
        {
            var daysLeft = (long)Math.Floor((c.ExpiresAt - now).TotalDays);
            var expired = c.ExpiresAt <= now;
            return new SystemAlertObservation(SourceId, c.Id.ToString("N"),
                expired ? NotificationSeverity.Critical : NotificationSeverity.Warning,
                expired ? $"Credential expired: {c.Name}" : $"Credential expiring: {c.Name} ({daysLeft}d)",
                expired ? $"Credential '{c.Name}' has expired." : $"Credential '{c.Name}' expires in {daysLeft} days.",
                "/credentials",
                new Dictionary<string, object?> { ["daysLeft"] = daysLeft, ["expired"] = expired },
                SignalValue: daysLeft);
        }).ToList();
    }
}
