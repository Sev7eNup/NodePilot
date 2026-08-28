using System.Net;
using Microsoft.Extensions.Options;
using NodePilot.Core.Net;

namespace NodePilot.Ai;

/// <summary>
/// The <see cref="IWebProxy"/> the LLM transport's <c>SocketsHttpHandler</c> is built with. It
/// resolves <c>Llm:Proxy:*</c> through a live <see cref="IOptionsMonitor{TOptions}"/> on every
/// request instead of at handler-construction time, which keeps the <c>Llm</c> settings section
/// hot-reloadable. In <see cref="LlmProxyMode.Off"/>, <see cref="IsBypassed"/> is true for every
/// destination, so the handler connects directly and <c>LlmConnectGuard</c> sees the real LLM host.
///
/// <para>Security trade-off: when a proxy carries the request, <c>ConnectCallback</c> runs against
/// the proxy endpoint and destination DNS is resolved by the proxy, so the connect-time link-local
/// and cloud-metadata guard no longer covers the destination. The literal <c>BaseUrl</c> check in
/// <see cref="LlmProfileValidation"/>, run on every settings save and at boot, covers it instead.
/// There is no mandatory allow-list as in <c>restApi</c>, because the LLM BaseUrl is a single
/// Admin-only value, not a per-step URL assembled from trigger payloads.</para>
/// </summary>
public sealed class LlmConfiguredProxy : IWebProxy
{
    private readonly IOptionsMonitor<LlmOptions> _options;

    public LlmConfiguredProxy(IOptionsMonitor<LlmOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Credentials the handler presents when the proxy answers 407. Resolved live, like everything
    /// else here. <see cref="LlmProxyOptions.UseDefaultCredentials"/> takes precedence over an
    /// explicit username, since a domain-integrated proxy is the usual reason to set it.
    /// </summary>
    public ICredentials? Credentials
    {
        get
        {
            var proxy = CurrentOptions;
            return proxy.Mode switch
            {
                LlmProxyMode.Off => null,
                LlmProxyMode.System => proxy.UseDefaultCredentials
                    ? CredentialCache.DefaultCredentials
                    : HttpClient.DefaultProxy.Credentials,
                LlmProxyMode.Custom => ResolveCustomCredentials(proxy),
                _ => null,
            };
        }

        // The interface requires a setter, but nothing in the HTTP stack assigns it. Throwing
        // avoids a silent no-op that would let a caller believe it had overridden the
        // configured credentials.
        set => throw new NotSupportedException(
            "LLM proxy credentials come from Llm:Proxy:* and cannot be assigned at runtime.");
    }

    /// <summary>Proxy to use for <paramref name="destination"/>, or <c>null</c> for a direct
    /// connection.</summary>
    public Uri? GetProxy(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        // A plaintext LLM endpoint is accepted only because it is on this host. Sending such a
        // request through a proxy would make "localhost" refer to the proxy machine and let the
        // unencrypted prompt and API key leave the loopback boundary.
        if (MustStayOnLoopback(destination))
            return null;

        var proxy = CurrentOptions;
        return proxy.Mode switch
        {
            LlmProxyMode.Off => null,
            LlmProxyMode.System => HttpClient.DefaultProxy.GetProxy(destination),
            LlmProxyMode.Custom => ResolveCustomProxy(proxy).GetProxy(destination),
            _ => null,
        };
    }

    /// <summary>True when <paramref name="destination"/> is reached without the proxy.</summary>
    public bool IsBypassed(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (MustStayOnLoopback(destination))
            return true;

        var proxy = CurrentOptions;
        return proxy.Mode switch
        {
            // Every destination bypasses the proxy, so the handler connects directly.
            LlmProxyMode.Off => true,
            LlmProxyMode.System => HttpClient.DefaultProxy.IsBypassed(destination),
            LlmProxyMode.Custom => ResolveCustomProxy(proxy).IsBypassed(destination),
            _ => true,
        };
    }

    private LlmProxyOptions CurrentOptions => _options.CurrentValue.Proxy ?? new LlmProxyOptions();

    private static bool MustStayOnLoopback(Uri destination)
        => destination.Scheme == Uri.UriSchemeHttp
            && LlmEndpointGuard.IsLiteralLoopbackEndpoint(destination);

    private static ICredentials? ResolveCustomCredentials(LlmProxyOptions proxy)
    {
        if (proxy.UseDefaultCredentials) return CredentialCache.DefaultCredentials;
        if (string.IsNullOrEmpty(proxy.Username)) return null;
        return new NetworkCredential(proxy.Username, proxy.Password ?? "");
    }

    /// <summary>
    /// Builds the <see cref="WebProxy"/> from the current settings on every call. The result is
    /// not cached: LLM requests are rate-limited, so one allocation and a few bypass regexes per
    /// request are cheaper than a cache invalidation mechanism.
    /// </summary>
    private static WebProxy ResolveCustomProxy(LlmProxyOptions proxy)
    {
        // The same two rules the settings validation applies, taken from LlmProfileValidation.
        if (!LlmProfileValidation.HasProxyAddress(proxy.Address, out var address))
        {
            // LlmProfileValidation rejects this on every save and at boot, so it is only reachable
            // through a hand-edited config picked up by hot-reload. Fail loudly instead of
            // silently connecting directly when a proxy was requested.
            throw new InvalidOperationException(
                $"{LlmProxyOptions.SectionName}:Mode is 'Custom' but {LlmProxyOptions.SectionName}:Address is empty. "
                + "Set a proxy URL (e.g. http://proxy.corp.local:8080) or switch the mode to 'Off' or 'System'.");
        }

        if (!LlmProfileValidation.IsHttpProxyUrl(address, out var proxyUri))
        {
            throw new InvalidOperationException(
                $"{LlmProxyOptions.SectionName}:Address '{address}' is not a valid http(s) URL.");
        }

        var bypass = (proxy.BypassList ?? new List<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .ToArray();

        return new WebProxy(
            proxyUri,
            BypassOnLocal: false,
            BypassList: bypass.Select(ProxyBypassPattern.ToRegex).ToArray())
        {
            Credentials = ResolveCustomCredentials(proxy),
        };
    }
}
