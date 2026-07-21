namespace NodePilot.Api.Dtos;

/// <summary>
/// Host identity returned by <c>GET /api/system/host-info</c> — surfaced in the SPA header
/// next to the API-connection indicator so any signed-in user can see which server they're
/// talking to. Available to every authenticated role (not just Admin); contains no secrets.
/// </summary>
/// <param name="MachineName">Windows machine (NetBIOS) name.</param>
/// <param name="Fqdn">Fully-qualified DNS name (or bare host label in a workgroup).</param>
/// <param name="Domain">DNS domain, or <c>null</c> when the host is not domain-joined.</param>
public sealed record HostInfoResponse(string MachineName, string Fqdn, string? Domain);
