using NodePilot.Core.Enums;

using System.Text.Json.Serialization;
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

/// <summary>What a recursive folder delete actually removed. The client computes an estimate up
/// front to show in the confirmation, but only this is the truth — folders the caller cannot read
/// still count, and the subtree may have changed between the two.</summary>
public record RecursiveFolderDeleteResponse(int DeletedFolders, int DeletedWorkflows);

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
    [property: JsonRequired] SharedFolderRole Role)
{
    public string? PrincipalAuthority { get; init; }
}

public record UpdateSharedFolderPermissionRequest(SharedFolderRole Role);
