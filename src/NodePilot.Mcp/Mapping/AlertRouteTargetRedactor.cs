using System.Diagnostics.CodeAnalysis;
using ModelContextProtocol;

namespace NodePilot.Mcp.Mapping;

/// <summary>
/// MCP-safe projection of alert route targets. Webhook URLs can carry credentials in user info,
/// path or query (for example chat-service tokens), so only scheme, host and port reach the agent.
/// Email targets are addresses, not credentials, and stay unchanged.
/// </summary>
public static class AlertRouteTargetRedactor
{
    public const string Mask = "***";

    [return: NotNullIfNotNull(nameof(target))]
    public static string? Redact(string? channel, string? target)
    {
        if (string.IsNullOrEmpty(target) || string.Equals(channel, "Email", StringComparison.OrdinalIgnoreCase))
            return target;
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return Mask;

        var origin = uri.IsDefaultPort ? $"{uri.Scheme}://{uri.Host}" : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
        var hasDetail = uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0;
        return hasDetail ? $"{origin}/{Mask}" : origin;
    }

    /// <summary>Rejects a redacted target sent back as a new route, which would replace the real URL.</summary>
    public static void RequireUnmasked(string webhookUrl)
    {
        if (webhookUrl.Contains(Mask, StringComparison.Ordinal))
            throw new McpException(
                $"Webhook URL '{webhookUrl}' is a redacted target. Omit webhooks to keep the current routes, or pass the full URL.");
    }
}
