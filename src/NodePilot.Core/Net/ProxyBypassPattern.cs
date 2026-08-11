using System.Text.RegularExpressions;

namespace NodePilot.Core.Net;

/// <summary>
/// Translates the operator-friendly proxy bypass patterns NodePilot accepts everywhere
/// (<c>RestApi:Proxy:BypassList</c>, <c>Llm:Proxy:BypassList</c>) into the regex form
/// <see cref="System.Net.WebProxy.BypassList"/> expects.
///
/// <para>Lives in Core because two independent outbound stacks need it and neither may
/// reference the other: <c>NodePilot.Engine</c> (restApi activity) and <c>NodePilot.Ai</c>
/// (LLM transport), where the dependency direction is Engine → Ai → Core.</para>
/// </summary>
public static class ProxyBypassPattern
{
    /// <summary>
    /// Convert a host pattern (<c>*.internal</c>, <c>api.corp</c>, <c>10.0.0.1</c>) to a regex.
    /// <see cref="System.Net.WebProxy"/> matches its bypass entries against the <b>full request
    /// URI</b> (scheme + host + port + path), not just the host — so the emitted expression
    /// anchors on the scheme and wraps the host pattern in optional port/path suffixes.
    /// Without that anchoring a bare <c>localhost</c> entry would never match anything.
    /// </summary>
    public static string ToRegex(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var escaped = Regex.Escape(pattern.Trim());
        // Regex.Escape turns "*" into "\*" — re-interpret as ".*" to support shell globs.
        escaped = escaped.Replace("\\*", ".*");
        return $@"^https?://{escaped}(:\d+)?(/.*)?$";
    }
}
