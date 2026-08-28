using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;

namespace NodePilot.Data;

/// <summary>
/// Default <see cref="IGlobalVariableFolderStore"/>. Owns the organizational folder tree for
/// global variables. Reparent, rename and delete apply the same rules as
/// <c>SharedWorkflowFoldersController</c> (cycle check, depth cap, sibling uniqueness, subtree
/// path recompute) without RBAC: folder access is Admin-gated at the controller.
/// </summary>
public class GlobalVariableFolderStore(NodePilotDbContext db) : IGlobalVariableFolderStore
{
    public async Task<IReadOnlyList<GlobalVariableFolderWithCount>> GetAllAsync(CancellationToken ct)
    {
        var folders = await db.GlobalVariableFolders.AsNoTracking()
            .OrderBy(f => f.Depth).ThenBy(f => f.Name)
            .ToListAsync(ct);

        var counts = await db.GlobalVariables.AsNoTracking()
            .GroupBy(v => v.FolderId)
            .Select(g => new { FolderId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.FolderId, x => x.Count, ct);

        return folders
            .Select(f => new GlobalVariableFolderWithCount(f, counts.GetValueOrDefault(f.Id, 0)))
            .ToList();
    }

    public Task<bool> ExistsAsync(Guid id, CancellationToken ct)
        => db.GlobalVariableFolders.AsNoTracking().AnyAsync(f => f.Id == id, ct);

    public async Task<GlobalVariableFolder> CreateAsync(Guid? parentFolderId, string name, Guid? createdByUserId, CancellationToken ct)
    {
        name = ValidateName(name);
        var parentId = parentFolderId ?? GlobalVariableFolder.RootFolderId;
        var parent = await db.GlobalVariableFolders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == parentId, ct)
            ?? throw new ArgumentException("Parent folder not found");

        if (parent.Depth + 1 > GlobalVariableFolder.MaxDepth)
            throw new InvalidOperationException($"Folder depth limit ({GlobalVariableFolder.MaxDepth}) would be exceeded");

        if (await db.GlobalVariableFolders.AnyAsync(f => f.ParentFolderId == parentId && f.Name == name, ct))
            throw new GlobalVariableFolderConflictException($"A folder named '{name}' already exists in the parent");

        var folder = new GlobalVariableFolder
        {
            Id = Guid.NewGuid(),
            ParentFolderId = parentId,
            Name = name,
            Path = parent.Path == "/" ? $"/{name}" : $"{parent.Path}/{name}",
            Depth = parent.Depth + 1,
            CreatedAt = DateTime.UtcNow,
            CreatedByUserId = createdByUserId,
        };
        db.GlobalVariableFolders.Add(folder);
        await db.SaveChangesAsync(ct);
        return folder;
    }

    public async Task<GlobalVariableFolder> RenameAsync(Guid id, string name, CancellationToken ct)
    {
        if (id == GlobalVariableFolder.RootFolderId)
            throw new InvalidOperationException("Root folder cannot be renamed");
        name = ValidateName(name);

        var folder = await db.GlobalVariableFolders.FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw new KeyNotFoundException($"Folder {id} not found");

        if (await db.GlobalVariableFolders.AnyAsync(f => f.ParentFolderId == folder.ParentFolderId && f.Name == name && f.Id != id, ct))
            throw new GlobalVariableFolderConflictException($"A sibling folder named '{name}' already exists");

        folder.Name = name;
        var all = await db.GlobalVariableFolders.ToListAsync(ct);
        RecomputePathsRecursive(folder, all);
        await db.SaveChangesAsync(ct);
        return folder;
    }

    public async Task<GlobalVariableFolder> MoveAsync(Guid id, Guid? newParentFolderId, CancellationToken ct)
    {
        if (id == GlobalVariableFolder.RootFolderId)
            throw new InvalidOperationException("Root folder cannot be moved");

        var folder = await db.GlobalVariableFolders.FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw new KeyNotFoundException($"Folder {id} not found");

        var newParentId = newParentFolderId ?? GlobalVariableFolder.RootFolderId;
        if (newParentId == folder.Id)
            throw new InvalidOperationException("A folder cannot be its own parent");

        var newParent = await db.GlobalVariableFolders.FirstOrDefaultAsync(f => f.Id == newParentId, ct)
            ?? throw new ArgumentException("Destination folder not found");

        var all = await db.GlobalVariableFolders.ToListAsync(ct);
        if (IsDescendant(all, ancestor: folder.Id, candidate: newParentId))
            throw new InvalidOperationException("Cannot move a folder into its own descendant");
        if (newParent.Depth + 1 + DescendantMaxDepth(all, folder.Id) > GlobalVariableFolder.MaxDepth)
            throw new InvalidOperationException($"Move would exceed folder depth limit ({GlobalVariableFolder.MaxDepth})");
        if (all.Any(f => f.ParentFolderId == newParentId && f.Name == folder.Name && f.Id != id))
            throw new GlobalVariableFolderConflictException($"A sibling named '{folder.Name}' already exists in the destination");

        folder.ParentFolderId = newParentId;
        folder.Depth = newParent.Depth + 1;
        RecomputePathsRecursive(folder, all);
        await db.SaveChangesAsync(ct);
        return folder;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        if (id == GlobalVariableFolder.RootFolderId)
            throw new InvalidOperationException("Root folder cannot be deleted");

        var folder = await db.GlobalVariableFolders.FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw new KeyNotFoundException($"Folder {id} not found");

        var hasChildren = await db.GlobalVariableFolders.AnyAsync(f => f.ParentFolderId == id, ct);
        var hasVariables = await db.GlobalVariables.AnyAsync(v => v.FolderId == id, ct);
        if (hasChildren || hasVariables)
            throw new GlobalVariableFolderConflictException("Folder is not empty — move or delete sub-folders and variables first");

        db.GlobalVariableFolders.Remove(folder);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Subtree delete. Mirrors the shared-workflow path, minus the edit-lock condition —
    /// globals have no checkout, so the only refusal is a concurrent change.
    ///
    /// The snapshot is taken inside the transaction and the delete is keyed on its ids: deleting
    /// by <c>FolderId</c> alone would also catch rows inserted after the snapshot, which would
    /// then vanish without an audit row and understate the reported count.
    /// </summary>
    public async Task<RecursiveGlobalFolderDeleteResult> DeleteRecursiveAsync(Guid id, CancellationToken ct)
    {
        if (id == GlobalVariableFolder.RootFolderId)
            throw new InvalidOperationException("Root folder cannot be deleted");

        var all = await db.GlobalVariableFolders.AsNoTracking().ToListAsync(ct);
        if (all.All(f => f.Id != id))
            throw new KeyNotFoundException($"Folder {id} not found");

        var subtreeIds = DescendantFolderIds(all, id);
        var doomedFolders = all
            .Where(f => subtreeIds.Contains(f.Id))
            .OrderByDescending(f => f.Depth)   // bottom-up: Folder→Folder is Restrict, not Cascade
            .ToList();

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var snapshot = await db.GlobalVariables.AsNoTracking()
                .Where(v => subtreeIds.Contains(v.FolderId))
                .Select(v => new DeletedGlobalVariable(v.Id, v.Name))
                .ToListAsync(ct);
            var snapshotIds = snapshot.Select(v => v.Id).ToList();

            // `subtreeIds.Contains` stays as a second condition: the row must still be where we
            // saw it, so one moved out from under us is not deleted by its stale id.
            var deleted = await db.GlobalVariables
                .Where(v => snapshotIds.Contains(v.Id) && subtreeIds.Contains(v.FolderId))
                .ExecuteDeleteAsync(ct);

            if (deleted != snapshotIds.Count)
            {
                await tx.RollbackAsync(ct);
                throw new GlobalVariableFolderConflictException(
                    "Folder contents changed while deleting — nothing was deleted, please try again");
            }

            // Anything that entered the subtree after the snapshot is still there. Refusing here
            // is an explicit check rather than a reliance on the Restrict FK firing, which is
            // provider-dependent; the catch below stays as the backstop for the same race on
            // sub-folders.
            var leftover = await db.GlobalVariables.CountAsync(v => subtreeIds.Contains(v.FolderId), ct);
            if (leftover > 0)
            {
                await tx.RollbackAsync(ct);
                throw new GlobalVariableFolderConflictException(
                    "Folder contents changed while deleting — nothing was deleted, please try again");
            }

            try
            {
                foreach (var doomed in doomedFolders)
                {
                    // Guarded on the parent we saw: a folder moved out of the subtree in the
                    // meantime must not be deleted by its stale id.
                    var removed = await db.GlobalVariableFolders
                        .Where(f => f.Id == doomed.Id && f.ParentFolderId == doomed.ParentFolderId)
                        .ExecuteDeleteAsync(ct);
                    if (removed != 1)
                    {
                        await tx.RollbackAsync(ct);
                        throw new GlobalVariableFolderConflictException(
                            "Folder contents changed while deleting — nothing was deleted, please try again");
                    }
                }
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateException)
            {
                await tx.RollbackAsync(ct);
                throw new GlobalVariableFolderConflictException(
                    "Folder contents changed while deleting — nothing was deleted, please try again");
            }

            return new RecursiveGlobalFolderDeleteResult(
                snapshot,
                doomedFolders.Select(f => new DeletedGlobalFolder(f.Id, f.Path)).ToList());
        });
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required");
        name = name.Trim();
        if (name.Length > 120)
            throw new ArgumentException("Name max length is 120 characters");
        return name;
    }

    // ---- Tree helpers (ported verbatim from SharedWorkflowFoldersController) ----

    private static void RecomputePathsRecursive(GlobalVariableFolder folder, List<GlobalVariableFolder> all)
    {
        var byParent = all.GroupBy(f => f.ParentFolderId).ToDictionary(g => g.Key ?? Guid.Empty, g => g.ToList());
        var stack = new Stack<GlobalVariableFolder>();
        stack.Push(folder);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current.ParentFolderId is null)
                current.Path = "/";
            else
            {
                var parent = all.FirstOrDefault(f => f.Id == current.ParentFolderId.Value);
                if (parent is not null)
                {
                    current.Path = parent.Path == "/" ? $"/{current.Name}" : $"{parent.Path}/{current.Name}";
                    current.Depth = parent.Depth + 1;
                }
            }
            if (byParent.TryGetValue(current.Id, out var children))
                foreach (var c in children) stack.Push(c);
        }
    }

    private static bool IsDescendant(List<GlobalVariableFolder> all, Guid ancestor, Guid candidate)
    {
        var byId = all.ToDictionary(f => f.Id);
        var current = candidate;
        var depth = 0;
        while (depth <= GlobalVariableFolder.MaxDepth + 1 && byId.TryGetValue(current, out var folder))
        {
            if (folder.ParentFolderId is null) return false;
            if (folder.ParentFolderId.Value == ancestor) return true;
            current = folder.ParentFolderId.Value;
            depth++;
        }
        return false;
    }

    /// <summary>The folder itself plus every descendant.</summary>
    private static HashSet<Guid> DescendantFolderIds(List<GlobalVariableFolder> all, Guid rootId)
    {
        var byParent = all.GroupBy(f => f.ParentFolderId)
            .ToDictionary(g => g.Key ?? Guid.Empty, g => g.ToList());
        var result = new HashSet<Guid>();
        var stack = new Stack<Guid>();
        stack.Push(rootId);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!result.Add(current)) continue;
            if (byParent.TryGetValue(current, out var children))
                foreach (var child in children) stack.Push(child.Id);
        }
        return result;
    }

    private static int DescendantMaxDepth(List<GlobalVariableFolder> all, Guid rootId)
    {
        var byParent = all.GroupBy(f => f.ParentFolderId).ToDictionary(g => g.Key ?? Guid.Empty, g => g.ToList());
        var max = 0;
        var stack = new Stack<(Guid id, int depth)>();
        stack.Push((rootId, 0));
        while (stack.Count > 0)
        {
            var (id, d) = stack.Pop();
            if (d > max) max = d;
            if (byParent.TryGetValue(id, out var children))
                foreach (var c in children) stack.Push((c.Id, d + 1));
        }
        return max;
    }
}
