namespace NodePilot.Cli.Api.Dtos;

// Mirrors src/NodePilot.Api/Dtos/WorkflowDtos.cs LoginRequest / LoginResponse.
public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResponse(string Token, Guid UserId, string Username, string Role);
public sealed record MeResponse(Guid Id, string Username, string Role);
