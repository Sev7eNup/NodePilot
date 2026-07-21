namespace NodePilot.Api.Dtos;

public record GlobalVariableResponse(
    Guid Id, string Name, string? Value, bool IsSecret, string? Description,
    Guid FolderId, DateTime CreatedAt, DateTime UpdatedAt, string? UpdatedBy);

public record CreateGlobalVariableRequest(string Name, string Value, bool IsSecret, string? Description, Guid? FolderId)
{
    // Mask on Value in case a debug log destructures the whole record.
    public override string ToString()
        => $"CreateGlobalVariableRequest {{ Name = {Name}, Value = {(IsSecret ? "***" : "<plain>")}, IsSecret = {IsSecret}, Description = {Description}, FolderId = {FolderId} }}";
}

public record UpdateGlobalVariableRequest(string Name, string? Value, bool IsSecret, string? Description, Guid? FolderId)
{
    public override string ToString()
        => $"UpdateGlobalVariableRequest {{ Name = {Name}, Value = {(Value is null ? "<unchanged>" : IsSecret ? "***" : "<plain>")}, IsSecret = {IsSecret}, Description = {Description}, FolderId = {FolderId} }}";
}

/// <summary>Move a global variable into a different organizational folder.</summary>
public record MoveGlobalVariableRequest(Guid FolderId);
