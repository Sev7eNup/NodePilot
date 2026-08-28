namespace NodePilot.Ai;

/// <summary>How the LLM transport reaches the outside world.</summary>
public enum LlmProxyMode
{
    /// <summary>Direct connection, no proxy consulted. Default.</summary>
    Off = 0,

    /// <summary>
    /// Use the proxy the operating system is configured with — on Windows the WinHTTP/WinINET
    /// settings of the account the NodePilot service runs under, including that configuration's
    /// own bypass rules.
    /// </summary>
    System = 1,

    /// <summary>Use <see cref="LlmProxyOptions.Address"/> and the settings next to it.</summary>
    Custom = 2,
}

/// <summary>
/// HTTP-proxy settings for every outbound LLM call, bound from <c>Llm:Proxy:*</c>. One block for
/// the whole feature rather than one per profile: mixed setups are handled by
/// <see cref="BypassList"/>, and a single block keeps one handler with one connection pool.
/// The default is <see cref="LlmProxyMode.Off"/>, so prompts are never routed through a proxy
/// nobody configured.
///
/// <para>With a proxy in the path the proxy resolves the destination's DNS, so the connect-time
/// SSRF guard (<c>LlmConnectGuard</c>) sees only the proxy endpoint. The destination stays covered
/// by the literal <c>BaseUrl</c> check that runs at boot and on every settings save.</para>
/// </summary>
public class LlmProxyOptions
{
    /// <summary>Configuration path of this block (<c>Llm:Proxy</c>).</summary>
    public const string SectionName = $"{LlmOptions.SectionName}:Proxy";

    /// <summary>Off (default) / System / Custom. See <see cref="LlmProxyMode"/>.</summary>
    public LlmProxyMode Mode { get; set; } = LlmProxyMode.Off;

    /// <summary>
    /// Proxy URL, e.g. <c>http://proxy.corp.local:8080</c>. Required when
    /// <see cref="Mode"/> is <see cref="LlmProxyMode.Custom"/>, ignored otherwise.
    /// </summary>
    public string? Address { get; set; }

    /// <summary>
    /// Hosts that skip the proxy. Accepts shell globs (<c>localhost</c>, <c>*.intern</c>,
    /// <c>10.0.0.1</c>). Only consulted in <see cref="LlmProxyMode.Custom"/>; in
    /// <see cref="LlmProxyMode.System"/> the operating system's own bypass list applies instead,
    /// so only one list is ever in effect.
    /// </summary>
    public List<string> BypassList { get; set; } = new();

    /// <summary>Username for a proxy that wants Basic auth. Empty means no explicit
    /// credentials.</summary>
    public string? Username { get; set; }

    /// <summary>
    /// Password for <see cref="Username"/>. Encrypted at rest like every other settings secret;
    /// a plaintext value in the config file raises a startup hardening warning.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Authenticate against the proxy with the service account's own Windows credentials
    /// (NTLM/Negotiate) instead of <see cref="Username"/>/<see cref="Password"/>. The usual
    /// setting for a domain-integrated corporate proxy. Applies to both
    /// <see cref="LlmProxyMode.System"/> and <see cref="LlmProxyMode.Custom"/>.
    /// </summary>
    public bool UseDefaultCredentials { get; set; }
}
