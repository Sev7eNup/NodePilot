namespace NodePilot.Cli.Api.Dtos;

// Mirrors src/NodePilot.Api/Dtos/WorkflowDtos.cs LoginRequest / LoginResponse.
public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResponse(
    string Token,
    Guid UserId,
    string Username,
    string Role,
    DateTimeOffset? ExpiresAt = null);
public sealed record MeResponse(Guid Id, string Username, string Role);

// Mirrors src/NodePilot.Api/Dtos/AuthDtos.cs AuthIdentityResponse — the body of the Windows
// SSO login, which never carries the JWT. That one arrives in the np_auth cookie.
public sealed record AuthIdentityResponse(Guid UserId, string Username, string Role);
