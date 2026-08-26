using NodePilot.Core.Models;

namespace NodePilot.Core.Interfaces;

/// <summary>
/// CRUD + tree operations for the organizational <see cref="GlobalVariableFolder"/> tree.
/// Structurally mirrors the shared-workflow-folder logic (cycle-safe reparent, depth cap,
/// sibling-name uniqueness, materialized path recompute) but carries <b>no</b> RBAC — global
/// variables are Admin-gated wholesale, so folder access is not per-folder authorized.
///
/// <para>Validation failures surface as typed exceptions the controller maps to HTTP status:
/// <see cref="KeyNotFoundException"/> → 404, <see cref="GlobalVariableFolderConflictException"/>
/// → 409 (sibling-name clash, non-empty delete), <see cref="InvalidOperationException"/> /
/// <see cref="ArgumentException"/> → 400 (depth cap, cycle, root-protected, bad parent).</para>
/// </summary>
public interface IGlobalVariableFolderStore
{
    /// <summary>The full tree (Root first, then by depth+name), each with its direct variable count.</summary>
    Task<IReadOnlyList<GlobalVariableFolderWithCount>> GetAllAsync(CancellationToken ct);

    /// <summary>True if a folder with this id exists — used to validate variable assignment.</summary>
    Task<bool> ExistsAsync(Guid id, CancellationToken ct);

    Task<GlobalVariableFolder> CreateAsync(Guid? parentFolderId, string name, Guid? createdByUserId, CancellationToken ct);

    Task<GlobalVariableFolder> RenameAsync(Guid id, string name, CancellationToken ct);

    Task<GlobalVariableFolder> MoveAsync(Guid id, Guid? newParentFolderId, CancellationToken ct);

    /// <summary>Deletes an empty folder. Throws <see cref="GlobalVariableFolderConflictException"/>
    /// if it still contains sub-folders or variables (move them out first).</summary>
    Task DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>Deletes a folder with its whole subtree: every descendant folder and every
    /// variable in them. Returns what was actually removed, so the caller can write one audit
    /// row per object. Throws <see cref="GlobalVariableFolderConflictException"/> (409) if the
    /// subtree changed while deleting — nothing is removed in that case.</summary>
    Task<RecursiveGlobalFolderDeleteResult> DeleteRecursiveAsync(Guid id, CancellationToken ct);
}

/// <summary>A folder plus the count of variables directly inside it (descendants excluded).</summary>
public sealed record GlobalVariableFolderWithCount(GlobalVariableFolder Folder, int VariableCount);

/// <summary>A deleted variable, identified for the audit trail. Carries the name only — a
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
