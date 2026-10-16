using System.Net.Http.Headers;
using System.Runtime.Versioning;
using NodePilot.Core.Clients;
using NodePilot.Mcp.Config;

namespace NodePilot.Mcp.Api;

/// <summary>
/// Builds the singleton <see cref="NodePilotApiClient"/> for the process from the resolved
/// <see cref="SessionContext"/>. A refreshable DPAPI session gets the <see
/// cref="TokenRefreshHandler"/>;
/// a raw env bearer does not. The client is always constructed (even unconfigured) so the server
/// can start and tools can return an actionable "run np auth login" error.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ApiClientFactory
{
    private readonly McpServerConfig _config;
    private readonly TokenStore _tokens;
    private readonly Func<ClientTlsOptions, HttpMessageHandler> _transport;

    public ApiClientFactory(McpServerConfig config, TokenStore tokens)
        : this(config, tokens, tls => PinnedCertificateHandlerFactory.Create(tls)) { }

    /// <summary>Test seam: <paramref name="transport"/> replaces the TLS-pinning network
    /// handler at the bottom of the chain.</summary>
    public ApiClientFactory(McpServerConfig config, TokenStore tokens, Func<ClientTlsOptions, HttpMessageHandler> transport)
    {
        _config = config;
        _tokens = tokens;
        _transport = transport;
    }

    public NodePilotApiClient Create()
    {
        var session = _config.Resolve();

        var transport = _transport(session.Tls);
        // Without a raw env bearer the CLI's DPAPI store is the token source. The handler reads
        // it per request, so a later `np auth login` takes effect without a restart, even when
        // no session existed at startup.
        if (session.UsesRefreshableSession || !session.HasToken)
        {
            transport = new TokenRefreshHandler(_tokens, session.Profile) { InnerHandler = transport };
        }

        var http = new HttpClient(transport, disposeHandler: true);

        http.Timeout = TimeSpan.FromSeconds(100);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NodePilot.Mcp", "1.0"));
        if (session.HasServer) http.BaseAddress = NormalizeBaseUri(session.Server!);

        var client = new NodePilotApiClient(http)
        {
            Session = session,
            // No server yet: resolve it again on use, so a server set by a later login is found.
            ResolveServer = session.HasServer
                ? null
                : () => _config.Resolve() is { HasServer: true } current ? NormalizeBaseUri(current.Server!) : null,
        };
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
