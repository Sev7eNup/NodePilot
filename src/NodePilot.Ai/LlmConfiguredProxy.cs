using System.Net;
using Microsoft.Extensions.Options;
using NodePilot.Core.Net;

namespace NodePilot.Ai;

/// <summary>
/// The <see cref="IWebProxy"/> the LLM transport's <c>SocketsHttpHandler</c> is built with. It
/// resolves <c>Llm:Proxy:*</c> on <b>every</b> request instead of at handler-construction time,
/// which is the whole point: <c>SocketsHttpHandler</c> owns the connection pool and is created
/// once per handler lifetime, so reading the proxy there would have made the <c>Llm</c> settings
/// section restart-required — the way <c>RestApi</c> is. Going through a live
/// <see cref="IOptionsMonitor{TOptions}"/> keeps the section hot-reloadable, kill-switch and all.
///
/// <para><b><see cref="LlmProxyMode.Off"/> is indistinguishable from the old
/// <c>UseProxy = false</c>:</b> <see cref="IsBypassed"/> answers <c>true</c> for every
/// destination, so the handler connects directly and <c>LlmConnectGuard</c> still sees the real
/// LLM host. That is what makes "no proxy configured" a genuine no-op rather than a new code
/// path.</para>
///
/// <para><b>Security trade-off in the two proxy modes.</b> Once a proxy carries the request, the
/// handler's <c>ConnectCallback</c> is invoked for the <i>proxy</i> endpoint — destination DNS is
/// resolved by the proxy, out of NodePilot's reach. The connect-time link-local/cloud-metadata
/// guard therefore stops covering the destination, which is left to the literal <c>BaseUrl</c>
/// check that <see cref="LlmProfileValidation"/> runs on every settings save and at boot.
/// Deliberately not countered with a mandatory allow-list the way <c>restApi</c> does it: the LLM
/// BaseUrl is one Admin-only value, not a per-step URL assembled from trigger payloads.</para>
/// </summary>
public sealed class LlmConfiguredProxy : IWebProxy
{
    private readonly IOptionsMonitor<LlmOptions> _options;

    /// <summary>
    /// Last built custom proxy plus the values it was built from. Rebuilding a
    /// <see cref="WebProxy"/> (and recompiling its bypass regexes) per request would be wasteful;
    /// comparing the source values is cheaper and needs no invalidation callback. A race just
    /// builds twice, which is harmless.
    /// </summary>
    private volatile CustomProxyCache? _cache;

    public LlmConfiguredProxy(IOptionsMonitor<LlmOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Credentials the handler presents when the proxy answers 407. Resolved live, like everything
    /// else here. <see cref="LlmProxyOptions.UseDefaultCredentials"/> wins over an explicit
    /// username because a domain-integrated proxy is the case operators reach for it.
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

        // The interface demands a setter; nothing in the HTTP stack assigns it (SocketsHttpHandler
        // only reads). Throwing beats a silent no-op that would make a caller believe it had
        // overridden the configured credentials.
        set => throw new NotSupportedException(
            "LLM proxy credentials come from Llm:Proxy:* and cannot be assigned at runtime.");
    }

    /// <summary>Proxy to use for <paramref name="destination"/>, or <c>null</c> for a direct connection.</summary>
    public Uri? GetProxy(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

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

        var proxy = CurrentOptions;
        return proxy.Mode switch
        {
            // Every destination bypasses → byte-for-byte the old UseProxy=false behaviour.
            LlmProxyMode.Off => true,
            LlmProxyMode.System => HttpClient.DefaultProxy.IsBypassed(destination),
            LlmProxyMode.Custom => ResolveCustomProxy(proxy).IsBypassed(destination),
            _ => true,
        };
    }

    private LlmProxyOptions CurrentOptions => _options.CurrentValue.Proxy ?? new LlmProxyOptions();

    private static ICredentials? ResolveCustomCredentials(LlmProxyOptions proxy)
    {
        if (proxy.UseDefaultCredentials) return CredentialCache.DefaultCredentials;
        if (string.IsNullOrEmpty(proxy.Username)) return null;
        return new NetworkCredential(proxy.Username, proxy.Password ?? "");
    }

    private WebProxy ResolveCustomProxy(LlmProxyOptions proxy)
    {
        var cached = _cache;
        if (cached is not null && cached.Matches(proxy)) return cached.Proxy;

        var address = proxy.Address?.Trim();
        if (string.IsNullOrWhiteSpace(address))
        {
            // Rejected by LlmProfileValidation on every save and at boot, so this only fires for a
            // hand-edited config picked up by hot-reload. Failing loudly beats silently going
            // direct when the operator asked for a proxy.
            throw new InvalidOperationException(
                $"{LlmProxyOptions.SectionName}:Mode is 'Custom' but {LlmProxyOptions.SectionName}:Address is empty. "
                + "Set a proxy URL (e.g. http://proxy.corp.local:8080) or switch the mode to 'Off' or 'System'.");
        }

        if (!Uri.TryCreate(address, UriKind.Absolute, out var proxyUri)
            || (proxyUri.Scheme != Uri.UriSchemeHttp && proxyUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"{LlmProxyOptions.SectionName}:Address '{address}' is not a valid http(s) URL.");
        }

        var bypass = (proxy.BypassList ?? new List<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .ToArray();

        var built = new WebProxy(
            proxyUri,
            BypassOnLocal: false,
            BypassList: bypass.Select(ProxyBypassPattern.ToRegex).ToArray())
        {
            Credentials = ResolveCustomCredentials(proxy),
        };

        _cache = new CustomProxyCache(built, address, bypass, proxy.Username, proxy.Password, proxy.UseDefaultCredentials);
        return built;
    }

    /// <summary>
    /// Snapshot of the values a cached <see cref="WebProxy"/> was built from. Compared field by
    /// field rather than via a concatenated signature string so the proxy password does not get a
    /// second, longer-lived copy in memory.
    /// </summary>
    private sealed record CustomProxyCache(
        WebProxy Proxy,
        string Address,
        string[] Bypass,
        string? Username,
        string? Password,
        bool UseDefaultCredentials)
    {
        public bool Matches(LlmProxyOptions options)
        {
            if (!string.Equals(Address, options.Address?.Trim(), StringComparison.Ordinal)) return false;
            if (!string.Equals(Username, options.Username, StringComparison.Ordinal)) return false;
            if (!string.Equals(Password, options.Password, StringComparison.Ordinal)) return false;
            if (UseDefaultCredentials != options.UseDefaultCredentials) return false;

            var incoming = (options.BypassList ?? new List<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim());
            return Bypass.SequenceEqual(incoming, StringComparer.Ordinal);
        }
    }
}
