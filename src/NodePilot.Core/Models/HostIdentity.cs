namespace NodePilot.Core.Models;

/// <summary>
/// Identity of the host the API process runs on. Surfaced to the UI so operators can see which
/// server answered a request, which matters in active/passive HA where any of several nodes may
/// be serving the SPA.
/// </summary>
/// <param name="MachineName">Windows machine (NetBIOS) name, e.g. <c>NPSRV01</c>.</param>
/// <param name="Fqdn">Fully-qualified DNS name, e.g. <c>npsrv01.corp.example.local</c>. Falls
/// back to the bare host label when no domain is configured.</param>
/// <param name="Domain">DNS domain the host is joined to, or <c>null</c> in a workgroup.</param>
public sealed record HostIdentity(string MachineName, string Fqdn, string? Domain);
