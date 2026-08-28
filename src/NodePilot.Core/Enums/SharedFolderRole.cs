namespace NodePilot.Core.Enums;

/// <summary>
/// Per-folder permission role granted to a principal. Roles are additive:
/// <see cref="FolderEditor"/> implies every right in <see cref="FolderOperator"/> and
/// <see cref="FolderViewer"/>.
/// <para>
/// Users with the global <c>Admin</c> role bypass folder permissions and hold implicit
/// <see cref="FolderAdmin"/> everywhere. Grants inherit down the tree: a grant on
/// <c>/finance</c> also covers <c>/finance/reports</c> unless the child folder overrides it.
/// </para>
/// </summary>
public enum SharedFolderRole
{
    /// <summary>List + read workflows in this folder and its sub-folders.</summary>
    FolderViewer = 0,

    /// <summary>FolderViewer + run/cancel/retry/resume workflow executions.</summary>
    FolderOperator = 1,

    /// <summary>
    /// FolderOperator + create/edit/delete/lock/publish/move workflows and create sub-folders.
    /// </summary>
    FolderEditor = 2,

    /// <summary>FolderEditor + grant/revoke folder permissions on this folder.</summary>
    FolderAdmin = 3,
}
