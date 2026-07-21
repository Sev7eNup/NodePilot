namespace NodePilot.Core.Enums;

/// <summary>
/// Type of principal that holds a <see cref="SharedFolderRole"/> on a folder. Schema-level
/// support for all three values is in V1; API + UI in V1 only allow <see cref="User"/>.
/// <see cref="Role"/> and <see cref="Group"/> are reserved for V2 + the OIDC integration —
/// without an external identity source there is no value in half-implemented group logic.
/// </summary>
public enum FolderPrincipalType
{
    User = 0,
    Role = 1,
    Group = 2,
}
