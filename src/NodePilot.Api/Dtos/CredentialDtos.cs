namespace NodePilot.Api.Dtos;

public record CreateCredentialRequest(string Name, string Username, string Password, string? Domain, DateTime? ExpiresAt = null)
{
    public override string ToString() => $"CreateCredentialRequest {{ Name = {Name}, Username = {Username}, Password = ***, Domain = {Domain}, ExpiresAt = {ExpiresAt:o} }}";
}

public record UpdateCredentialRequest(string Name, string Username, string? Password, string? Domain, DateTime? ExpiresAt = null)
{
    public override string ToString() => $"UpdateCredentialRequest {{ Name = {Name}, Username = {Username}, Password = {(Password is null ? "null" : "***")}, Domain = {Domain}, ExpiresAt = {ExpiresAt:o} }}";
}

public record CredentialResponse(Guid Id, string Name, string Username, string? Domain, DateTime? ExpiresAt = null);
