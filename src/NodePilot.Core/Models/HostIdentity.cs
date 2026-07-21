namespace NodePilot.Core.Models;

/// <summary>
/// Identity of the host the API process runs on, surfaced to the UI so operators can see
/// <em>which</em> server answered their request (especially relevant in active/passive HA,
/// where any of several nodes might be the one serving the SPA).
/// </summary>
/// <param name="MachineName">Windows machine (NetBIOS) name, e.g. <c>NPSRV01</c>.</param>
/// <param name="Fqdn">Fully-qualified DNS name, e.g. <c>npsrv01.corp.example.local</c>.
/// Falls back to the bare host label in workgroup setups where no domain is configured.</param>
/// <param name="Domain">DNS domain the host is joined to, or <c>null</c> in a workgroup.</param>
public sealed record HostIdentity(string MachineName, string Fqdn, string? Domain);
