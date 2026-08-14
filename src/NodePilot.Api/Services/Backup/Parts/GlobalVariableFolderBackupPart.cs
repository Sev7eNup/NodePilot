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

        return new JsonObject
        {
            ["structure"] = FolderTrees.Structure(folders, FolderTrees.Global),
        };
    }
}
