using System.Net.Http.Headers;
using System.Runtime.Versioning;
using NodePilot.Mcp.Auth;
using NodePilot.Mcp.Config;

namespace NodePilot.Mcp.Api;

/// <summary>
/// Builds the singleton <see cref="NodePilotApiClient"/> for the process from the resolved
/// <see cref="SessionContext"/>. A refreshable DPAPI session gets the <see cref="TokenRefreshHandler"/>;
/// a raw env bearer does not. The client is always constructed (even unconfigured) so the server
/// can start and tools can return an actionable "run np auth login" error.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ApiClientFactory
{
    private readonly McpServerConfig _config;
    private readonly TokenStore _tokens;

    public ApiClientFactory(McpServerConfig config, TokenStore tokens)
    {
        _config = config;
        _tokens = tokens;
    }

    public NodePilotApiClient Create()
    {
        var session = _config.Resolve();

        HttpClient http;
        if (session.UsesRefreshableSession)
        {
            var refresher = new TokenRefreshHandler(_tokens, session.Profile) { InnerHandler = new HttpClientHandler() };
            http = new HttpClient(refresher, disposeHandler: true);
        }
        else
        {
            http = new HttpClient();
        }

        http.Timeout = TimeSpan.FromSeconds(100);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NodePilot.Mcp", "1.0"));
        if (session.HasServer) http.BaseAddress = NormalizeBaseUri(session.Server!);

        var client = new NodePilotApiClient(http) { Session = session };
        if (session.HasToken) client.BearerToken = session.Token;
        return client;
    }

    private static Uri NormalizeBaseUri(string raw)
    {
        var trimmed = raw.Trim();
        if (!trimmed.EndsWith('/')) trimmed += "/";
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("NodePilot server URL must be an absolute HTTPS URL.");
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Refusing insecure NodePilot server URL '{uri}'. MCP bearer tokens may only be sent over HTTPS.");
        return uri;
    }
}
