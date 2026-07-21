using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Interfaces;
using NodePilot.Data;

namespace NodePilot.Api.Services.Backup.Parts;

/// <summary>
/// Exports alerting rules — both custom rules and system policies (every <c>NotificationRule</c>) — with their
/// routes and scope targets (ADR 0008, schema v2). Route secrets are decrypted with the active
/// <see cref="ISecretProtector"/> and re-wrapped under the backup passphrase, exactly like credentials. The
/// delivery ledger, suppression, signal and policy-state tables are transient and deliberately excluded.
/// DependsOn Folders + Workflows so scope targets remap on restore.
/// </summary>
public sealed class AlertingBackupPart(NodePilotDbContext db, ISecretProtector atRest) : IBackupPart
{
    public string Key => BackupSections.Alerting;
    public IReadOnlyList<string> DependsOn => [BackupSections.Folders, BackupSections.Workflows];

    public Task<int> CountAsync(CancellationToken ct) => db.NotificationRules.CountAsync(ct);

    public async Task<JsonNode> ExportAsync(BackupExportContext ctx, CancellationToken ct)
    {
        var rules = await db.NotificationRules.AsNoTracking()
            .Include(r => r.Routes)
            .Include(r => r.Targets)
            .OrderBy(r => r.Name)
            .ToListAsync(ct);

        var items = new JsonArray();
        foreach (var r in rules)
        {
            var routes = new JsonArray();
            foreach (var route in r.Routes.OrderBy(x => x.Order))
            {
                var node = new JsonObject
                {
                    ["channel"] = route.Channel.ToString(),
                    ["target"] = route.Target,
                    ["order"] = route.Order,
                    ["conditionExpressionJson"] = route.ConditionExpressionJson,
                };
                if (!string.IsNullOrEmpty(route.Secret))
                {
                    try
                    {
                        var plaintext = atRest.Unprotect(Convert.FromBase64String(route.Secret));
                        node["secret"] = ctx.Enc(plaintext);
                    }
                    catch (Exception ex) when (ex is CryptographicException or FormatException)
                    {
                        node["secretUnavailable"] = true;
                        ctx.Warn($"Alerting route secret for '{r.Name}' could not be decrypted on this host — exported without it.");
                    }
                }
                routes.Add(node);
            }

            var targets = new JsonArray();
            foreach (var t in r.Targets)
                targets.Add(new JsonObject { ["targetKind"] = t.TargetKind.ToString(), ["targetId"] = t.TargetId.ToString() });

            items.Add(new JsonObject
            {
                ["name"] = r.Name,
                ["description"] = r.Description,
                ["isEnabled"] = r.IsEnabled,
                ["kind"] = r.Kind.ToString(),
                ["eventTypes"] = r.EventTypes,
                ["filterExpressionJson"] = r.FilterExpressionJson,
                ["scopeKind"] = r.ScopeKind.ToString(),
                ["cooldownMinutes"] = r.CooldownMinutes,
                ["dedupKeyTemplate"] = r.DedupKeyTemplate,
                ["minOccurrences"] = r.MinOccurrences,
                ["occurrenceWindowMinutes"] = r.OccurrenceWindowMinutes,
                ["systemSourceId"] = r.SystemSourceId,
                ["systemPresetId"] = r.SystemPresetId,
                ["sourceParametersJson"] = r.SourceParametersJson,
                ["sustainForSeconds"] = r.SustainForSeconds,
                ["severityOverride"] = r.SeverityOverride?.ToString(),
                ["routes"] = routes,
                ["targets"] = targets,
            });
        }
        return new JsonObject { ["items"] = items };
    }
}
