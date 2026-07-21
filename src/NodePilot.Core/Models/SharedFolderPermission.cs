using NodePilot.Core.Enums;

namespace NodePilot.Core.Models;

/// <summary>
/// Grant of a <see cref="SharedFolderRole"/> on a <see cref="SharedWorkflowFolder"/> to
/// a principal. Permissions inherit down the folder tree: a grant on <c>/finance</c>
/// applies to <c>/finance/reports</c> unless overridden. Higher-role-wins when multiple
/// grants resolve to the same user (direct + via inheritance + Group memberships).
/// <para>
/// <see cref="PrincipalKey"/> is a string so it can hold both User-Guids and AD-Group-SIDs.
/// V1 supports User and Group principals; Role-typed grants are reserved.
/// </para>
/// </summary>
public class SharedFolderPermission
{
    public Guid Id { get; set; }
    public Guid FolderId { get; set; }
    public FolderPrincipalType PrincipalType { get; set; } = FolderPrincipalType.User;

    /// <summary>
    /// Namespace that owns a group principal. User grants use an empty string. Historic
    /// group grants with an empty value are interpreted as Active Directory and are
    /// normalized by the enterprise-SSO migration.
    /// </summary>
    public string PrincipalAuthority { get; set; } = string.Empty;

    /// <summary>
    /// Identifier of the granted principal as a string.
    /// <list type="bullet">
    ///   <item><description><see cref="FolderPrincipalType.User"/>: <see cref="User.Id"/>
    ///         formatted as <c>Guid.ToString("D")</c> (lowercase, hyphenated).</description></item>
    ///   <item><description><see cref="FolderPrincipalType.Group"/>: provider-stable group
    ///         key within <see cref="PrincipalAuthority"/> (AD SID, OIDC or SCIM group id).</description></item>
    ///   <item><description><see cref="FolderPrincipalType.Role"/>: reserved for future use.</description></item>
    /// </list>
    /// Renamed from <c>PrincipalId Guid</c> → <c>PrincipalKey string</c> in PR0a (LDAP groundwork)
    /// because Group-SIDs don't fit a Guid column.
    /// </summary>
    public string PrincipalKey { get; set; } = string.Empty;

    public SharedFolderRole Role { get; set; }

    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;
    public Guid? GrantedByUserId { get; set; }

    public SharedWorkflowFolder Folder { get; set; } = null!;
}
