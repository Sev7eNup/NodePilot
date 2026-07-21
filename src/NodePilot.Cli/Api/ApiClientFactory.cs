using System.Net.Http.Headers;
using System.Runtime.Versioning;
using NodePilot.Cli.Auth;

namespace NodePilot.Cli.Api;

/// <summary>
/// Builds a <see cref="NodePilotApiClient"/> wired with the right base address, bearer
/// token and refresh handler for a given resolved <see cref="SessionContext"/>.
/// Commands resolve this from DI rather than newing up HttpClients themselves.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ApiClientFactory
{
    private readonly TokenStore _tokens;

    public ApiClientFactory(TokenStore tokens) => _tokens = tokens;

    public NodePilotApiClient Create(SessionContext session, bool requireAuth = true)
    {
        if (!session.HasServer)
            throw new InvalidOperationException(
                "No server URL configured. Run `np config set server <URL>` or pass --server.");
        if (requireAuth && !session.HasSession)
            throw new NotAuthenticatedException(
                $"Not authenticated for profile '{session.Profile}'. Run `np auth login`.");

        var baseUri = NormalizeBaseUri(session.Server!, session.AllowInsecureLoopback);
        var primary = new HttpClientHandler();
        HttpMessageHandler handler = primary;
        if (session.HasSession)
        {
            handler = new TokenRefreshHandler(_tokens, session.Profile)
            {
                InnerHandler = primary,
            };
        }

        var http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(60),
        };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NodePilot.Cli", "1.0"));

        var client = new NodePilotApiClient(http);
        if (session.HasSession) client.BearerToken = session.Session!.Token;
        return client;
    }

    /// <summary>
    /// Build a client without an attached session — used by `np auth login` itself,
    /// which has nowhere yet to read a token from.
    /// </summary>
    public NodePilotApiClient CreateAnonymous(string serverUrl, bool allowInsecureLoopback = false)
    {
        var http = new HttpClient
        {
            BaseAddress = NormalizeBaseUri(serverUrl, allowInsecureLoopback),
            Timeout = TimeSpan.FromSeconds(60),
        };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NodePilot.Cli", "1.0"));
        return new NodePilotApiClient(http);
    }

    private static Uri NormalizeBaseUri(string raw, bool allowInsecureLoopback)
    {
        var trimmed = raw.Trim();
        if (!trimmed.EndsWith('/')) trimmed += "/";
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("NodePilot server URL must be an absolute HTTPS URL.");
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !(allowInsecureLoopback
                 && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                 && uri.IsLoopback))
            throw new InvalidOperationException(
                $"Refusing insecure NodePilot server URL '{uri}'. Use HTTPS, or pass --allow-insecure explicitly for an HTTP loopback development server.");
        return uri;
    }
}

public sealed class NotAuthenticatedException : Exception
{
    public NotAuthenticatedException(string message) : base(message) { }
}
