namespace NodePilot.Api.Security.Ldap;

public sealed record LdapDirectorySnapshot(
    string Subject,
    bool IsEnabled,
    string Upn,
    string DisplayName,
    IReadOnlyList<string> GroupSids);

public sealed record LdapEndpointHealth(
    bool Healthy,
    string? Endpoint,
    string? ErrorCode,
    bool Degraded = false,
    IReadOnlyList<string>? HealthyEndpoints = null,
    IReadOnlyList<string>? FailedEndpoints = null);
