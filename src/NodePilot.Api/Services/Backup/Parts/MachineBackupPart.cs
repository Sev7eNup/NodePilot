using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NodePilot.Data;

namespace NodePilot.Api.Services.Backup.Parts;

/// <summary>
/// Exports managed machines. <c>DefaultCredentialId</c> is kept verbatim and remapped on restore
/// via the credential-map (which requires Credentials to restore first — ADR 0001 K4). No secrets
/// live on the machine row itself. Transient connectivity fields are not exported.
/// </summary>
public sealed class MachineBackupPart(NodePilotDbContext db) : IBackupPart
{
    public string Key => BackupSections.Machines;
    public IReadOnlyList<string> DependsOn => [BackupSections.Credentials];

    public Task<int> CountAsync(CancellationToken ct) => db.ManagedMachines.CountAsync(ct);

    public async Task<JsonNode> ExportAsync(BackupExportContext ctx, CancellationToken ct)
    {
        var machines = await db.ManagedMachines.AsNoTracking().OrderBy(m => m.Name).ToListAsync(ct);
        var items = new JsonArray();
        foreach (var m in machines)
        {
            items.Add(new JsonObject
            {
                ["sourceId"] = m.Id.ToString(),
                ["name"] = m.Name,
                ["hostname"] = m.Hostname,
                ["winRmPort"] = m.WinRmPort,
                ["useSsl"] = m.UseSsl,
                ["defaultCredentialId"] = m.DefaultCredentialId?.ToString(),
                ["tags"] = m.Tags,
            });
        }
        return new JsonObject { ["items"] = items };
    }
}
