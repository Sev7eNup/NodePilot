using System.Text.Json.Nodes;
using NodePilot.Core.Interfaces;

namespace NodePilot.Api.Services.Backup.Parts;

/// <summary>
/// Exports custom-activity definitions (the live row of each, including disabled drafts). The
/// PowerShell <c>scriptTemplate</c> is encrypted as one opaque value under the backup passphrase.
/// Like a workflow's runScript field it can contain arbitrary legacy literals that no key-name
/// heuristic can classify safely. Version-history snapshots are
/// intentionally excluded (DR snapshot = live config, not history). Restored faithfully with their
/// enabled state — unlike the dedicated <c>.npca</c> import, which forces disabled.
/// </summary>
public sealed class CustomActivityBackupPart(ICustomActivityDefinitionStore store) : IBackupPart
{
    public string Key => BackupSections.CustomActivities;
    public IReadOnlyList<string> DependsOn => [];

    public async Task<int> CountAsync(CancellationToken ct) =>
        (await store.GetAllAsync(includeDisabled: true, ct)).Count;

    public async Task<JsonNode> ExportAsync(BackupExportContext ctx, CancellationToken ct)
    {
        var all = await store.GetAllAsync(includeDisabled: true, ct);
        var items = new JsonArray();
        foreach (var d in all.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            items.Add(new JsonObject
            {
                ["sourceId"] = d.Id.ToString(),
                ["key"] = d.Key,
                ["name"] = d.Name,
                ["description"] = d.Description,
                ["icon"] = d.Icon,
                ["color"] = d.Color,
                ["scriptTemplate"] = ctx.Enc(d.ScriptTemplate),
                ["engine"] = d.Engine,
                ["runsRemote"] = d.RunsRemote,
                ["isolated"] = d.Isolated,
                ["memoryLimitMb"] = d.MemoryLimitMb,
                ["maxProcesses"] = d.MaxProcesses,
                ["defaultTimeoutSeconds"] = d.DefaultTimeoutSeconds,
                ["successExitCodes"] = d.SuccessExitCodes,
                // Defaults are injected as runtime PowerShell variables and may contain the same
                // arbitrary legacy literals as the script itself. Seal the complete schema blob;
                // output metadata does not carry executable input values.
                ["inputParametersJson"] = ctx.Enc(d.InputParametersJson),
                ["outputParametersJson"] = d.OutputParametersJson,
                ["isEnabled"] = d.IsEnabled,
                ["version"] = d.Version,
            });
        }
        return new JsonObject { ["items"] = items };
    }
}
