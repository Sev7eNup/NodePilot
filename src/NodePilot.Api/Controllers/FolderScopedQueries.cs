using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;

namespace NodePilot.Api.Controllers;

/// <summary>
/// Folder-RBAC scoping for the list/aggregate endpoints. The three-step shape — global Admin
/// passes through unrestricted, a caller with zero accessible folders is a dead end, everyone
/// else gets the folder IN-filter — was restated at every call site. Only the dead-end answer
/// differs per endpoint (404, empty list, empty dashboard, always-false query), so it stays with
/// the caller: a <c>null</c> return means "this caller can read no folder at all".
/// </summary>
internal static class FolderScopedQueries
{
    public static IQueryable<Workflow>? ScopeToAccessibleFolders(
        this IQueryable<Workflow> query, AccessibleFolderSet accessible)
    {
        if (accessible.IsUnrestricted) return query;
        if (accessible.FolderIds.Count == 0) return null;
        return query.Where(w => accessible.FolderIds.Contains(w.FolderId));
    }

    /// <summary>
    /// Execution variant. Inner-join semantics: pulls each execution's workflow folder via the
    /// navigation property. Translates to a single JOIN on Postgres + SQL Server.
    /// </summary>
    public static IQueryable<WorkflowExecution>? ScopeToAccessibleFolders(
        this IQueryable<WorkflowExecution> query, AccessibleFolderSet accessible)
    {
        if (accessible.IsUnrestricted) return query;
        if (accessible.FolderIds.Count == 0) return null;
        return query.Where(e => accessible.FolderIds.Contains(e.Workflow.FolderId));
    }
}
