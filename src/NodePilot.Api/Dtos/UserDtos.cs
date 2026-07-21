namespace NodePilot.Api.Dtos;

public record UserResponse(
    Guid Id,
    string Username,
    string Role,
    bool IsActive,
    DateTime CreatedAt,
    string Provider = "Local",
    string? Authority = null,
    string? Subject = null,
    DateTime? LastDirectorySyncAt = null,
    string DirectorySyncStatus = "Never",
    bool IsTombstoned = false,
    bool IsBreakGlass = false);

public record CreateUserRequest(string Username, string Password, string Role, bool IsBreakGlass = false)
{
    public override string ToString() => $"CreateUserRequest {{ Username = {Username}, Password = ***, Role = {Role} }}";
}

public record UpdateUserRequest(string? Role, bool? IsActive, string? Password, bool? IsBreakGlass = null)
{
    public override string ToString() => $"UpdateUserRequest {{ Role = {Role}, IsActive = {IsActive}, Password = {(Password is null ? "null" : "***")} }}";
}
