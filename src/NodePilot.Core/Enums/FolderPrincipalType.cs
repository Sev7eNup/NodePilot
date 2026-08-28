namespace NodePilot.Core.Enums;

/// <summary>
/// Type of principal that holds a <see cref="SharedFolderRole"/> on a folder. The schema
/// supports all three values, but API and UI currently accept only <see cref="User"/>.
/// <see cref="Role"/> and <see cref="Group"/> are reserved for the group-based grants that
/// need an external identity source.
/// </summary>
public enum FolderPrincipalType
{
    User = 0,
    Role = 1,
    Group = 2,
}
