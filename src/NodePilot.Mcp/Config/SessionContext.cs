using NodePilot.Core.Clients;

namespace NodePilot.Mcp.Config;

/// <summary>
/// Resolved connection context for one MCP server process. Built once at startup by
/// <see cref="McpServerConfig.Resolve"/>.
/// </summary>
/// <param name="Server">Base API URL, or null when nothing is configured.</param>
/// <param name="Profile">Profile name used for the DPAPI session lookup + token refresh.</param>
/// <param name="Token">Bearer token, or null when unauthenticated.</param>
/// <param name="UsesRefreshableSession">
/// True when the token came from the DPAPI store (auto-refresh on 401 is wired up).
/// False for a raw <c>NODEPILOT_MCP_TOKEN</c> env bearer (no refresh — it just expires).
/// </param>
/// <param name="TlsThumbprint">
/// Canonical SHA-256 fingerprint accepted in addition to a valid certificate chain, for a server
/// whose certificate this machine does not trust.
/// </param>
/// <param name="SkipTlsVerification">Certificate validation bypass from the environment.</param>
/// <param name="TlsConfigurationError">
/// Set when a configured pin cannot be used. The server still starts — stdio has to come up — but
/// every API call is refused until the pin is fixed, because dropping it would silently downgrade
/// a pinned connection.
/// </param>
public sealed record SessionContext(
    string? Server,
    string Profile,
    string? Token,
    bool UsesRefreshableSession,
    string? TlsThumbprint = null,
    bool SkipTlsVerification = false,
    string? TlsConfigurationError = null)
{
    public bool HasServer => !string.IsNullOrWhiteSpace(Server);
    public bool HasToken => !string.IsNullOrWhiteSpace(Token);

    public ClientTlsOptions Tls => new(TlsThumbprint, SkipTlsVerification);
}
