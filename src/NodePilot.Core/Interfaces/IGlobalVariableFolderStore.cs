using NodePilot.Core.Models;

namespace NodePilot.Core.Interfaces;

/// <summary>
/// CRUD and tree operations for the <see cref="GlobalVariableFolder"/> tree: cycle-safe
/// reparent, depth cap, sibling-name uniqueness, materialized path recompute. Carries no RBAC,
/// because global variables are Admin-gated as a whole.
///
/// <para>Validation failures surface as typed exceptions the controller maps to HTTP status:
/// <see cref="KeyNotFoundException"/> is 404, <see cref="GlobalVariableFolderConflictException"/>
/// is 409, and both <see cref="InvalidOperationException"/> and <see cref="ArgumentException"/>
/// are 400.</para>
/// </summary>
public interface IGlobalVariableFolderStore
{
    /// <summary>The full tree (Root first, then depth+name) with direct variable counts.</summary>
    Task<IReadOnlyList<GlobalVariableFolderWithCount>> GetAllAsync(CancellationToken ct);

    /// <summary>True if a folder with this id exists. Used when assigning a variable.</summary>
    Task<bool> ExistsAsync(Guid id, CancellationToken ct);

    Task<GlobalVariableFolder> CreateAsync(Guid? parentFolderId, string name, Guid? createdByUserId, CancellationToken ct);

    Task<GlobalVariableFolder> RenameAsync(Guid id, string name, CancellationToken ct);

    Task<GlobalVariableFolder> MoveAsync(Guid id, Guid? newParentFolderId, CancellationToken ct);

    /// <summary>Deletes an empty folder. Throws <see cref="GlobalVariableFolderConflictException"/>
    /// if it still contains sub-folders or variables (move them out first).</summary>
    Task DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>Deletes a folder with its whole subtree: every descendant folder and every
    /// variable in them. Returns what was removed, so the caller can write one audit row per
    /// object. Throws <see cref="GlobalVariableFolderConflictException"/> (409) if the subtree
    /// changed while deleting; nothing is removed in that case.</summary>
    Task<RecursiveGlobalFolderDeleteResult> DeleteRecursiveAsync(Guid id, CancellationToken ct);
}

/// <summary>A folder plus the count of variables directly inside it (not descendants).</summary>
public sealed record GlobalVariableFolderWithCount(GlobalVariableFolder Folder, int VariableCount);

/// <summary>A deleted variable, identified for the audit trail. Carries the name only, because a
/// global's <c>Value</c> is a secret and never leaves the store on a delete path.</summary>
public sealed record DeletedGlobalVariable(Guid Id, string Name);

/// <summary>A deleted folder, identified for the audit trail.</summary>
public sealed record DeletedGlobalFolder(Guid Id, string Path);

/// <summary>What a recursive folder delete committed. The caller turns this into audit rows and
/// the response counts, so both describe the same set.</summary>
public sealed record RecursiveGlobalFolderDeleteResult(
    IReadOnlyList<DeletedGlobalVariable> Variables,
    IReadOnlyList<DeletedGlobalFolder> Folders);

/// <summary>Thrown for folder operations that violate an invariant a caller should resolve
/// (sibling-name clash, deleting a non-empty folder). Mapped to HTTP 409.</summary>
public sealed class GlobalVariableFolderConflictException(string message) : Exception(message);
