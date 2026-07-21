namespace NodePilot.Engine.Options;

/// <summary>
/// HTTP-proxy settings for the outgoing <c>restApi</c> activity, bound from
/// <c>RestApi:Proxy:*</c>. When <see cref="Enabled"/> is <c>false</c> (default), the named
/// NodePilot HttpClient explicitly sets <c>UseProxy=false</c> so it does not silently pick
/// up the Windows system proxy — that would route workflow traffic through whatever the
/// user account happens to have configured, which surprised operators in an early lab
/// deployment.
/// </summary>
/// <remarks>
/// Per-step overrides (<c>proxyMode</c>, <c>proxyAddress</c>, <c>noProxy</c>) are resolved
/// separately in <c>RestApiHttpClientProvider</c>'s per-request handler factory; this POCO
/// only carries the default handler settings.
/// </remarks>
public sealed class RestApiProxyOptions
{
    public const string SectionName = "RestApi:Proxy";

    public bool Enabled { get; set; }
    public string? Address { get; set; }
    /// <summary>
    /// Host patterns that bypass the proxy. Accepts shell globs (<c>*.internal</c>,
    /// <c>10.0.0.1</c>). Empty list = no bypass.
    /// </summary>
    public List<string> BypassList { get; set; } = new();
    public string? Username { get; set; }
    public string? Password { get; set; }
}
