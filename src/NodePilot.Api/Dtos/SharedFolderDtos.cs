using NodePilot.Core.Enums;

namespace NodePilot.Api.Dtos;

public record SharedFolderResponse(
    Guid Id,
    Guid? ParentFolderId,
    string Name,
    string Path,
    int Depth,
    DateTime CreatedAt,
    Guid? CreatedByUserId,
    int WorkflowCount,
    SharedFolderCapabilities Capabilities);

public record SharedFolderCapabilities(bool CanRead, bool CanRun, bool CanEdit, bool CanAdmin);

public record CreateSharedFolderRequest(Guid? ParentFolderId, string Name);

public record UpdateSharedFolderRequest(string Name);

public record MoveSharedFolderRequest(Guid? NewParentFolderId);

public record MoveWorkflowToFolderRequest(Guid TargetFolderId);

public record SharedFolderPermissionResponse(
    Guid Id,
    Guid FolderId,
    FolderPrincipalType PrincipalType,
    // PrincipalKey: User-Guid as Guid.ToString("D") for User-grants, AD-Group-SID for Group-grants.
    string PrincipalKey,
    // PrincipalDisplayName: Resolved username for User-grants; group display-name for Group-grants
    // (can be null when the SID isn't resolvable from the local AD cache).
    string? PrincipalDisplayName,
    SharedFolderRole Role,
    DateTime GrantedAt,
    Guid? GrantedByUserId)
{
    public string? PrincipalAuthority { get; init; }
}

public record GrantSharedFolderPermissionRequest(
    FolderPrincipalType PrincipalType,
    string PrincipalKey,
    SharedFolderRole Role)
{
    public string? PrincipalAuthority { get; init; }
}

public record UpdateSharedFolderPermissionRequest(SharedFolderRole Role);
