namespace NodePilot.Api.Dtos;

/// <summary>
/// A folder in the global-variable organization tree. <see cref="VariableCount"/> is the number
/// of variables directly in this folder (not including descendants), for the tree display.
/// </summary>
public record GlobalVariableFolderResponse(
    Guid Id, Guid? ParentFolderId, string Name, string Path, int Depth,
    DateTime CreatedAt, Guid? CreatedByUserId, int VariableCount);

public record CreateGlobalVariableFolderRequest(Guid? ParentFolderId, string Name);

public record UpdateGlobalVariableFolderRequest(string Name);

public record MoveGlobalVariableFolderRequest(Guid? NewParentFolderId);
