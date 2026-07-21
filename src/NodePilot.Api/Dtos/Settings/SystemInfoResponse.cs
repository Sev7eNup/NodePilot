namespace NodePilot.Api.Dtos.Settings;

/// <summary>
/// Read-only system-info snapshot returned by <c>GET /api/admin/settings/system-info</c>.
/// Surfaces the configuration that lives below the Settings UI's reach — bootstrap-only
/// values that an operator can't edit through the UI but still wants to inspect.
/// </summary>
/// <param name="AppVersion">Informational assembly version of <c>NodePilot.Api</c>.</param>
/// <param name="OverridesPath">Absolute path of the active <c>appsettings.runtime.json</c> file.</param>
/// <param name="DatabaseProvider">Active EF Core provider — <c>postgres</c> or <c>sqlserver</c>.</param>
/// <param name="DatabaseHost">Host extracted from the connection string; redacted when not parseable.</param>
/// <param name="SecretsProvider">Active <see cref="NodePilot.Core.Interfaces.ISecretProtector"/> name (<c>Dpapi</c> / <c>AesGcm</c> / migrating wrapper).</param>
/// <param name="ClusterEnabled">True when active/passive HA is wired up.</param>
/// <param name="ClusterNodeId">Stable node identifier reported by <c>IClusterStateProvider</c>.</param>
/// <param name="ClusterIsLeader">Read-only leadership state. Always <c>true</c> in single-node mode.</param>
/// <param name="JwtIssuer">Configured <c>Jwt:Issuer</c>; <c>NodePilot</c> default when unset.</param>
/// <param name="JwtAudience">Configured <c>Jwt:Audience</c>; <c>NodePilot</c> default when unset.</param>
public sealed record SystemInfoResponse(
    string AppVersion,
    string OverridesPath,
    string DatabaseProvider,
    string? DatabaseHost,
    string SecretsProvider,
    bool ClusterEnabled,
    string ClusterNodeId,
    bool ClusterIsLeader,
    string JwtIssuer,
    string JwtAudience);
