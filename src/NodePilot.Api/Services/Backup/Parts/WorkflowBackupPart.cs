using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NodePilot.Data;

namespace NodePilot.Api.Services.Backup.Parts;

/// <summary>
/// Exports workflow definitions. Inline secret config values (<c>secret</c>, <c>apiKey</c>, …) are
/// encrypted for backup via <see cref="WorkflowDefinitionSecretRewriter"/>; the <c>targetMachineId</c>
/// / <c>credentialId</c> GUID references are left verbatim and remapped on restore (K3/K13). Each item
/// carries its original <c>sourceId</c> so restore can build the workflow id-map.
/// </summary>
public sealed class WorkflowBackupPart(NodePilotDbContext db) : IBackupPart
{
    public string Key => BackupSections.Workflows;
    public IReadOnlyList<string> DependsOn => [BackupSections.Folders, BackupSections.Machines, BackupSections.Credentials];

    public Task<int> CountAsync(CancellationToken ct) => db.Workflows.CountAsync(ct);

    public async Task<JsonNode> ExportAsync(BackupExportContext ctx, CancellationToken ct)
    {
        var workflows = await db.Workflows.AsNoTracking().OrderBy(w => w.Name).ToListAsync(ct);
        var items = new JsonArray();
        foreach (var w in workflows)
        {
            JsonNode definition;
            try
            {
                using var doc = JsonDocument.Parse(w.DefinitionJson);
                definition = WorkflowDefinitionSecretRewriter.Rewrite(
                    doc.RootElement, SecretHandling.EncryptForBackup, ctx.Protector);
            }
            catch (JsonException)
            {
                ctx.Warn($"Workflow '{w.Name}' has invalid DefinitionJson — exported as empty definition.");
                definition = new JsonObject { ["nodes"] = new JsonArray(), ["edges"] = new JsonArray() };
            }

            items.Add(new JsonObject
            {
                ["sourceId"] = w.Id.ToString(),
                ["name"] = w.Name,
                ["description"] = w.Description,
                ["isEnabled"] = w.IsEnabled,
                ["folderId"] = w.FolderId.ToString(),
                ["version"] = w.Version,
                ["definition"] = definition,
            });
        }
        return new JsonObject { ["items"] = items };
    }
}
