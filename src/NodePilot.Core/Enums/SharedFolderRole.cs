namespace NodePilot.Core.Enums;

/// <summary>
/// Per-folder permission role granted to a principal (User in V1, Role/Group reserved for
/// V2 + OIDC integration). Roles are strictly additive: <see cref="FolderEditor"/> implies
/// every right in <see cref="FolderOperator"/> and <see cref="FolderViewer"/>.
/// <para>
/// Globally <c>Admin</c>-roled users bypass folder permissions entirely and have implicit
/// <see cref="FolderAdmin"/> on every folder. Folder permissions are inherited down the
/// tree: a grant on <c>/finance</c> applies to <c>/finance/reports</c> too unless the
/// child folder explicitly overrides.
/// </para>
/// </summary>
public enum SharedFolderRole
{
    /// <summary>List + read workflows in this folder and its sub-folders.</summary>
    FolderViewer = 0,

    /// <summary>FolderViewer + run/cancel/retry/resume workflow executions.</summary>
    FolderOperator = 1,

    /// <summary>FolderOperator + create/edit/delete/lock/publish/move workflows + create sub-folders.</summary>
    FolderEditor = 2,

    /// <summary>FolderEditor + grant/revoke folder permissions on this folder.</summary>
    FolderAdmin = 3,
}
