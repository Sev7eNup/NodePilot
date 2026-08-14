using System.Text.Json.Nodes;
using NodePilot.Core.Models;

namespace NodePilot.Api.Services.Backup;

/// <summary>
/// Accessors over the two structurally identical folder trees — <see cref="SharedWorkflowFolder"/>
/// and <see cref="GlobalVariableFolder"/>. Both carry Id / ParentFolderId / Name / Path / Depth /
/// CreatedByUserId with the same semantics (singleton Root, materialized Path, sibling-unique Name),
/// but they share no interface in NodePilot.Core, so backup export and restore reach the fields
/// through delegates. Core follow-up: give both entities one folder-node interface and delete this
/// shim. <c>Apply</c> writes the mutable columns in one go: name, path, depth, parent id, creator.
/// </summary>
internal sealed record FolderTreeShape<TFolder>(
    Func<TFolder, Guid> Id,
    Func<TFolder, Guid?> ParentId,
    Func<TFolder, string> Name,
    Func<TFolder, string> Path,
    Func<TFolder, int> Depth,
    Func<TFolder, Guid?> CreatedBy,
    Func<Guid, TFolder> New,
    Action<TFolder, string, string, int, Guid?, Guid?> Apply);

/// <summary>The concrete shapes plus the export projection both folder sections share.</summary>
internal static class FolderTrees
{
    public static readonly FolderTreeShape<SharedWorkflowFolder> Shared = new(
        folder => folder.Id,
        folder => folder.ParentFolderId,
        folder => folder.Name,
        folder => folder.Path,
        folder => folder.Depth,
        folder => folder.CreatedByUserId,
        id => new SharedWorkflowFolder { Id = id },
        (folder, name, path, depth, parentId, createdBy) =>
        {
            folder.Name = name;
            folder.Path = path;
            folder.Depth = depth;
            folder.ParentFolderId = parentId;
            folder.CreatedByUserId = createdBy;
        });

    public static readonly FolderTreeShape<GlobalVariableFolder> Global = new(
        folder => folder.Id,
        folder => folder.ParentFolderId,
        folder => folder.Name,
        folder => folder.Path,
        folder => folder.Depth,
        folder => folder.CreatedByUserId,
        id => new GlobalVariableFolder { Id = id },
        (folder, name, path, depth, parentId, createdBy) =>
        {
            folder.Name = name;
            folder.Path = path;
            folder.Depth = depth;
            folder.ParentFolderId = parentId;
            folder.CreatedByUserId = createdBy;
        });

    /// <summary>
    /// The <c>structure</c> array of a folder section. The caller supplies the folders already
    /// ordered by Depth then Name — restore relies on every parent preceding its children.
    /// </summary>
    public static JsonArray Structure<TFolder>(
        IEnumerable<TFolder> folders,
        FolderTreeShape<TFolder> shape)
    {
        var structure = new JsonArray();
        foreach (var f in folders)
        {
            structure.Add(new JsonObject
            {
                ["sourceId"] = shape.Id(f).ToString(),
                ["parentFolderId"] = shape.ParentId(f)?.ToString(),
                ["name"] = shape.Name(f),
                ["path"] = shape.Path(f),
                ["depth"] = shape.Depth(f),
                ["createdByUserId"] = shape.CreatedBy(f)?.ToString(),
            });
        }
        return structure;
    }
}
