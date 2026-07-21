using System.Text.Json.Nodes;
using NodePilot.Core.Interfaces;

namespace NodePilot.Api.Services.Backup.Parts;

/// <summary>
/// Exports global variables. Non-secret values go in cleartext; secret values are resolved to
/// plaintext (via the store's decrypt path) and rewrapped under the backup passphrase. A secret
/// that cannot be decrypted on this host is exported flagged <c>valueUnavailable</c> with a warning.
/// </summary>
public sealed class GlobalVariableBackupPart(IGlobalVariableStore store) : IBackupPart
{
    public string Key => BackupSections.GlobalVariables;
    // Variables carry a folderId → auto-include the folder tree so the reference resolves.
    public IReadOnlyList<string> DependsOn => [BackupSections.GlobalVariableFolders];

    public async Task<int> CountAsync(CancellationToken ct) => (await store.GetAllAsync(ct)).Count;

    public async Task<JsonNode> ExportAsync(BackupExportContext ctx, CancellationToken ct)
    {
        var all = await store.GetAllAsync(ct);
        var resolved = await store.GetAllResolvedDetailedAsync(ct);

        var items = new JsonArray();
        foreach (var v in all.OrderBy(v => v.Name, StringComparer.Ordinal))
        {
            var item = new JsonObject
            {
                ["sourceId"] = v.Id.ToString(),
                ["name"] = v.Name,
                ["isSecret"] = v.IsSecret,
                ["description"] = v.Description,
                ["folderId"] = v.FolderId.ToString(),
            };

            if (!v.IsSecret)
            {
                item["value"] = v.Value; // stored plaintext
            }
            else if (resolved.Resolved.TryGetValue(v.Name, out var plaintext))
            {
                item["value"] = ctx.Enc(plaintext);
            }
            else
            {
                item["valueUnavailable"] = true;
                ctx.Warn($"Global variable '{v.Name}' is a secret that could not be decrypted on this host — exported without value.");
            }

            items.Add(item);
        }
        return new JsonObject { ["items"] = items };
    }
}
