using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NodePilot.Data;

namespace NodePilot.Api.Services.Backup.Parts;

/// <summary>
/// Exports the shared-folder tree and its RBAC grants as two separate arrays — <c>structure</c>
/// (folders, including the singleton Root) and <c>grants</c>
/// (<see cref="NodePilot.Core.Models.SharedFolderPermission"/>). They're split because restore
/// has to apply them in a fixed order: folders need the Users section restored first (grants
/// reference user ids), and grants need the folder structure restored first (ADR 0001, section
/// K4). User-id references (<c>createdByUserId</c>, <c>grantedByUserId</c>, and User-typed
/// <c>principalKey</c>) are kept verbatim and remapped on restore via the user-map; AD-group
/// SIDs pass through unchanged.
/// </summary>
public sealed class FolderBackupPart(NodePilotDbContext db) : IBackupPart
{
    public string Key => BackupSections.Folders;
    public IReadOnlyList<string> DependsOn => [BackupSections.Users];

    public Task<int> CountAsync(CancellationToken ct) => db.SharedWorkflowFolders.CountAsync(ct);

    public async Task<JsonNode> ExportAsync(BackupExportContext ctx, CancellationToken ct)
    {
        var folders = await db.SharedWorkflowFolders.AsNoTracking().OrderBy(f => f.Depth).ThenBy(f => f.Name).ToListAsync(ct);
        var grants = await db.SharedFolderPermissions.AsNoTracking().ToListAsync(ct);

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

        var grantArr = new JsonArray();
        foreach (var g in grants)
        {
            grantArr.Add(new JsonObject
            {
                ["folderId"] = g.FolderId.ToString(),
                ["principalType"] = g.PrincipalType.ToString(),
                ["principalAuthority"] = g.PrincipalType == NodePilot.Core.Enums.FolderPrincipalType.Group
                    ? (string.IsNullOrWhiteSpace(g.PrincipalAuthority)
                        ? NodePilot.Core.Models.ExternalIdentity.ActiveDirectoryAuthority
                        : g.PrincipalAuthority)
                    : null,
                ["principalKey"] = g.PrincipalKey,
                ["role"] = g.Role.ToString(),
                ["grantedByUserId"] = g.GrantedByUserId?.ToString(),
            });
        }

        return new JsonObject { ["structure"] = structure, ["grants"] = grantArr };
    }
}
