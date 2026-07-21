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
public sealed record SessionContext(string? Server, string Profile, string? Token, bool UsesRefreshableSession)
{
    public bool HasServer => !string.IsNullOrWhiteSpace(Server);
    public bool HasToken => !string.IsNullOrWhiteSpace(Token);

    internal static bool HasSameServerOrigin(string? left, string? right)
    {
        if (!Uri.TryCreate(left?.Trim(), UriKind.Absolute, out var leftUri)
            || !Uri.TryCreate(right?.Trim(), UriKind.Absolute, out var rightUri))
        {
            return false;
        }

        return string.Equals(leftUri.Scheme, rightUri.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(leftUri.IdnHost, rightUri.IdnHost, StringComparison.OrdinalIgnoreCase)
               && leftUri.Port == rightUri.Port;
    }
}
