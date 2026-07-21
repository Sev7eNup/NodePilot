using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NodePilot.Data;

namespace NodePilot.Api.Services.Backup.Parts;

/// <summary>
/// Exports the global-variable folder tree (organizational only — folders never change how
/// <c>{{globals.NAME}}</c> resolves). One <c>structure</c> array including the singleton Root.
/// Mirrors <see cref="FolderBackupPart"/> minus the RBAC grants. <c>createdByUserId</c> is kept
/// verbatim and remapped on restore via the user-map.
/// </summary>
public sealed class GlobalVariableFolderBackupPart(NodePilotDbContext db) : IBackupPart
{
    public string Key => BackupSections.GlobalVariableFolders;
    public IReadOnlyList<string> DependsOn => [BackupSections.Users];

    public Task<int> CountAsync(CancellationToken ct) => db.GlobalVariableFolders.CountAsync(ct);

    public async Task<JsonNode> ExportAsync(BackupExportContext ctx, CancellationToken ct)
    {
        var folders = await db.GlobalVariableFolders.AsNoTracking()
            .OrderBy(f => f.Depth).ThenBy(f => f.Name).ToListAsync(ct);

        var structure = new JsonArray();
        foreach (var f in folders)
        {
            structure.Add(new JsonObject
            {
                ["sourceId"] = f.Id.ToString(),
                ["parentFolderId"] = f.ParentFolderId?.ToString(),
                ["name"] = f.Name,
                ["path"] = f.Path,
                ["depth"] = f.Depth,
                ["createdByUserId"] = f.CreatedByUserId?.ToString(),
            });
        }

        return new JsonObject { ["structure"] = structure };
    }
}
