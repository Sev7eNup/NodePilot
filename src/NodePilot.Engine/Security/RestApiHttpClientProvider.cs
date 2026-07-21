using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NodePilot.Engine.Options;

namespace NodePilot.Engine.Security;

/// <summary>
/// Resolves the HttpClient + primary handler for <c>RestApiActivity</c> with proxy awareness.
///
/// Three modes per step:
///   default — use the named "NodePilot" client (configured at startup from RestApi:Proxy:*)
///   direct  — force no proxy (override global)
///   custom  — use a step-local proxy address + bypass list
///
/// Per-step override handlers are cached in-process: <c>SocketsHttpHandler</c> owns the
/// connection pool, so recreating it per request would exhaust sockets. Cache is keyed by
/// the full proxy signature and bounded (MaxCachedHandlers) to keep memory flat for
/// pathological workflows that put a new proxy in every step.
/// </summary>
public sealed class RestApiHttpClientProvider
{
    private const int MaxCachedHandlers = 32;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IWebProxy? _defaultProxy;
    private readonly ConcurrentDictionary<string, SocketsHttpHandler> _overrideHandlers = new();

    public RestApiHttpClientProvider(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IOptions<RestApiProxyOptions>? defaultProxyOptions = null)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;

        // Keep the policy snapshot aligned with the IOptions instance used by Program.cs to
        // construct the named handler. RestApi:Proxy is restart-required; consulting live
        // IConfiguration here would let a saved-but-not-restarted value disagree with the
        // handler that is actually carrying traffic.
        var configured = defaultProxyOptions?.Value ?? ReadDefaultProxyOptions(configuration);
        if (configured.Enabled)
        {
            if (string.IsNullOrWhiteSpace(configured.Address))
                throw new InvalidOperationException(
                    "RestApi:Proxy:Enabled is true but RestApi:Proxy:Address is empty. " +
                    "Set a proxy URL (e.g. http://proxy.corp.local:8080) or disable the proxy.");

            _defaultProxy = CreateProxy(
                configured.Address,
                (configured.BypassList ?? new List<string>())
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v.Trim())
                    .ToArray(),
                configured.Username,
                configured.Password);
        }
    }

    /// <summary>
    /// Build the primary message handler for the named "NodePilot" HttpClient from
    /// <c>RestApi:Proxy:*</c>. Called once at startup via <c>ConfigurePrimaryHttpMessageHandler</c>.
    /// The <paramref name="configuration"/> is captured by the SocketsHttpHandler's
    /// ConnectCallback so the SSRF policy is re-evaluated at TCP-connect time for direct
    /// and no-proxy-bypassed requests. A forward proxy hides destination DNS from the
    /// callback, so RestApiActivity separately requires exact destination allow-listing.
    /// Keeping this static lets Program.cs call it without resolving the provider.
    /// </summary>
    public static SocketsHttpHandler BuildDefaultHandler(RestApiProxyOptions opts, IConfiguration configuration)
    {
        if (opts.Enabled && string.IsNullOrWhiteSpace(opts.Address))
            throw new InvalidOperationException(
                "RestApi:Proxy:Enabled is true but RestApi:Proxy:Address is empty. " +
                "Set a proxy URL (e.g. http://proxy.corp.local:8080) or disable the proxy.");

        var bypass = (opts.BypassList ?? new List<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .ToArray();

        return BuildHandler(
            useProxy: opts.Enabled,
            address: opts.Enabled ? opts.Address : null,
            bypassPatterns: bypass,
            username: opts.Username,
            password: opts.Password,
            configuration: configuration);
    }

    /// <summary>
    /// Return the HttpClient a step should use. The returned client is safe to dispose
    /// (either factory-owned, or a thin wrapper over a cached handler with
    /// disposeHandler: false).
    /// </summary>
    public HttpClient GetClient(JsonElement stepConfig)
    {
        var mode = ReadMode(stepConfig);
        if (mode == ProxyMode.Default)
            return _httpClientFactory.CreateClient("NodePilot");

        var handler = ResolveOverrideHandler(stepConfig, mode);
        return new HttpClient(handler, disposeHandler: false);
    }

    /// <summary>
    /// Enforces the extra policy required when a forward proxy will carry this particular
    /// request. In proxy mode the handler's ConnectCallback sees only the proxy endpoint;
    /// destination DNS is resolved by the proxy and therefore cannot be pinned or filtered
    /// locally at connect time. Exact administrator allow-listing is the fail-closed boundary.
    /// Direct and no-proxy-bypassed destinations continue to use NetworkGuard's IP policy.
    /// </summary>
    internal void ValidateDestinationPolicy(JsonElement stepConfig, Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!UsesProxyForDestination(stepConfig, destination)) return;
        if (NetworkGuard.IsHostAllowlisted(_configuration, destination.Host)) return;

        throw new InvalidOperationException(
            $"REST API proxy: destination host '{destination.Host}' is not explicitly allowed. " +
            "Add the exact host to RestApi:AllowedHosts. Proxied workflow destinations are " +
            "denied by default because the proxy, not NodePilot's guarded ConnectCallback, resolves destination DNS.");
    }

    internal bool UsesProxyForDestination(JsonElement stepConfig, Uri destination)
    {
        var mode = ReadMode(stepConfig);
        if (mode == ProxyMode.Direct) return false;

        IWebProxy? proxy;
        if (mode == ProxyMode.Custom)
        {
            var handler = ResolveOverrideHandler(stepConfig, mode);
            proxy = handler.UseProxy ? handler.Proxy : null;
        }
        else
        {
            proxy = _defaultProxy;
        }

        return proxy is not null && !proxy.IsBypassed(destination);
    }

    /// <summary>
    /// Internal for tests — lets the unit suite inspect the concrete SocketsHttpHandler
    /// (UseProxy, Proxy.Address, cache-reuse) without opening sockets.
    /// </summary>
    internal SocketsHttpHandler ResolveOverrideHandler(JsonElement stepConfig, ProxyMode? forceMode = null)
    {
        var mode = forceMode ?? ReadMode(stepConfig);
        if (mode == ProxyMode.Default)
            throw new InvalidOperationException(
                "ResolveOverrideHandler called for Default mode — use the named client instead.");

        var address = mode == ProxyMode.Custom && stepConfig.TryGetProperty("proxyAddress", out var a)
            ? a.GetString() : null;
        var bypass = mode == ProxyMode.Custom ? ReadNoProxy(stepConfig) : Array.Empty<string>();

        if (mode == ProxyMode.Custom && string.IsNullOrWhiteSpace(address))
            throw new InvalidOperationException(
                "REST API: proxyMode=\"custom\" requires a non-empty proxyAddress.");

        var key = BuildKey(mode, address, bypass);
        return _overrideHandlers.GetOrAdd(key, _ =>
        {
            if (_overrideHandlers.Count >= MaxCachedHandlers)
            {
                // Defensive: a workflow with hundreds of unique proxies per step would
                // otherwise grow the cache without bound. Clearing is simpler than an LRU
                // and sufficient — repopulation is cheap.
                _overrideHandlers.Clear();
            }
            return BuildHandler(
                useProxy: mode == ProxyMode.Custom,
                address: address,
                bypassPatterns: bypass,
                username: null,
                password: null,
                configuration: _configuration);
        });
    }

    private static SocketsHttpHandler BuildHandler(
        bool useProxy,
        string? address,
        string[] bypassPatterns,
        string? username,
        string? password,
        IConfiguration configuration)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = useProxy,
            // SSRF guard at TCP-connect time. For a direct/no-proxy-bypassed request the
            // endpoint is the destination and this closes the DNS-rebinding window. For a
            // proxied request the endpoint is only the proxy; RestApiActivity therefore
            // requires the actual request/redirect host in RestApi:AllowedHosts.
            ConnectCallback = (ctx, ct) => ConnectWithSsrfGuardAsync(ctx, configuration, ct),
        };

        if (useProxy && !string.IsNullOrWhiteSpace(address))
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var proxyUri)
                || (proxyUri.Scheme != Uri.UriSchemeHttp && proxyUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    $"REST API: proxy address '{address}' is not a valid http(s) URL.");
            }

            handler.Proxy = CreateProxy(proxyUri, bypassPatterns, username, password);
        }

        return handler;
    }

    /// <summary>
    /// Custom <c>ConnectCallback</c> that re-applies the SSRF policy at the moment of TCP
    /// connect. Resolves the host (or accepts the literal IP), filters the resolved
    /// addresses through <see cref="NetworkGuard.EnforceConnect"/>, and then opens a TCP
    /// socket to the first surviving address. <see cref="NetworkGuard.EnforceConnect"/>
    /// throws <see cref="IOException"/> when the entire address set is blocked, which
    /// surfaces through HttpClient as a transport failure on the originating SendAsync —
    /// the typical shape of a connect refusal.
    ///
    /// Internal so the engine tests can drive it without spinning up a real handler.
    /// </summary>
    internal static async ValueTask<Stream> ConnectWithSsrfGuardAsync(
        SocketsHttpConnectionContext ctx,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var endPoint = ctx.DnsEndPoint;
        var host = endPoint.Host;
        var port = endPoint.Port;

        IPAddress[] resolved;
        if (IPAddress.TryParse(host, out var direct))
        {
            resolved = new[] { direct };
        }
        else
        {
            resolved = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }

        var allowed = NetworkGuard.EnforceConnect(configuration, host, resolved);

        // Use a TCP socket directly so we can pin the connect to the post-filter address
        // set. Socket.ConnectAsync(IPAddress[], int) already does Happy-Eyeballs-ish ordering
        // (first that connects wins) over the addresses we hand it, which is sufficient.
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
        {
            // Disable Nagle for HTTP — matches the default SocketsHttpHandler behaviour and
            // avoids head-of-line latency on small request bodies.
            NoDelay = true,
        };
        try
        {
            await socket.ConnectAsync(allowed, port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Convert a user-friendly host pattern ("*.internal", "api.corp", "10.0.0.1") to a
    /// regex suitable for <see cref="WebProxy.BypassList"/>. WebProxy matches patterns
    /// against the full request URI (scheme + host + port + path), not just the host, so
    /// the emitted regex anchors on scheme and wraps the host pattern with optional
    /// port/path suffixes.
    /// </summary>
    internal static string ConvertBypassToRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern.Trim());
        // Regex.Escape turns "*" into "\*" — re-interpret as ".*" to support shell globs.
        escaped = escaped.Replace("\\*", ".*");
        return $@"^https?://{escaped}(:\d+)?(/.*)?$";
    }

    private static WebProxy CreateProxy(
        string address,
        string[] bypassPatterns,
        string? username,
        string? password)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var proxyUri)
            || (proxyUri.Scheme != Uri.UriSchemeHttp && proxyUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"REST API: proxy address '{address}' is not a valid http(s) URL.");
        }

        return CreateProxy(proxyUri, bypassPatterns, username, password);
    }

    private static WebProxy CreateProxy(
        Uri proxyUri,
        string[] bypassPatterns,
        string? username,
        string? password)
    {
        var regexPatterns = bypassPatterns.Select(ConvertBypassToRegex).ToArray();
        var proxy = new WebProxy(proxyUri, BypassOnLocal: false, BypassList: regexPatterns);
        if (!string.IsNullOrEmpty(username))
            proxy.Credentials = new NetworkCredential(username, password ?? "");
        return proxy;
    }

    private static RestApiProxyOptions ReadDefaultProxyOptions(IConfiguration configuration)
    {
        var enabled = bool.TryParse(configuration["RestApi:Proxy:Enabled"], out var parsed) && parsed;
        var bypass = configuration.GetSection("RestApi:Proxy:BypassList").GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .ToList();
        return new RestApiProxyOptions
        {
            Enabled = enabled,
            Address = configuration["RestApi:Proxy:Address"],
            BypassList = bypass,
            Username = configuration["RestApi:Proxy:Username"],
            Password = configuration["RestApi:Proxy:Password"],
        };
    }

    private static ProxyMode ReadMode(JsonElement cfg)
    {
        if (cfg.ValueKind != JsonValueKind.Object) return ProxyMode.Default;
        if (!cfg.TryGetProperty("proxyMode", out var m) || m.ValueKind != JsonValueKind.String)
            return ProxyMode.Default;
        return (m.GetString() ?? "").ToLowerInvariant() switch
        {
            "custom" => ProxyMode.Custom,
            "direct" => ProxyMode.Direct,
            _ => ProxyMode.Default,
        };
    }

    private static string[] ReadNoProxy(JsonElement cfg)
    {
        if (!cfg.TryGetProperty("noProxy", out var np)) return Array.Empty<string>();
        if (np.ValueKind == JsonValueKind.String)
        {
            return (np.GetString() ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        if (np.ValueKind == JsonValueKind.Array)
        {
            return np.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!.Trim())
                .Where(s => s.Length > 0)
                .ToArray();
        }
        return Array.Empty<string>();
    }

    private static string BuildKey(ProxyMode mode, string? address, string[] bypass)
    {
        var sb = new StringBuilder();
        sb.Append(mode).Append('|').Append(address ?? "");
        sb.Append('|');
        foreach (var b in bypass.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            sb.Append(b).Append(',');
        return sb.ToString();
    }

    internal enum ProxyMode { Default, Direct, Custom }
}
