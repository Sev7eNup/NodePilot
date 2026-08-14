using System.Net;
using System.Net.Sockets;

namespace NodePilot.Ai;

/// <summary>
/// Single validation point for an LLM endpoint <c>BaseUrl</c> — whether it comes from the global
/// <c>Llm:*</c> config or a per-node <see cref="LlmConnection"/> override. Enforces an absolute
/// http/https URL and rejects cloud-metadata endpoints, and — via
/// <see cref="ResolveEndpoint"/> — is also the single place that decides which wire dialect the
/// endpoint speaks and what URL to POST to. Used by <see cref="ILlmClientFactory"/>,
/// the <c>llmQuery</c> activity, the settings boot-validator and the settings test-probe so there
/// is no unguarded LLM-egress path. The complementary TCP-connect-time DNS-rebinding guard lives
/// in <c>LlmConnectGuard</c> (the <see cref="System.Net.Http.SocketsHttpHandler"/> ConnectCallback).
/// </summary>
public static class LlmEndpointGuard
{
    private const string ResponsesSuffix = "/responses";
    private const string ChatCompletionsSuffix = "/chat/completions";

    /// <summary>
    /// Validates <paramref name="baseUrl"/> (see <see cref="NormalizeAndValidateBaseUrl"/>) and
    /// derives the wire dialect plus the concrete POST target from its path:
    /// <list type="bullet">
    /// <item>ends in <c>/responses</c> → <see cref="LlmApiFlavor.Responses"/>, POSTed verbatim</item>
    /// <item>ends in <c>/chat/completions</c> → <see cref="LlmApiFlavor.ChatCompletions"/>, POSTed
    /// verbatim (an operator pasting the full endpoint URL must not get it appended twice)</item>
    /// <item>anything else → <see cref="LlmApiFlavor.ChatCompletions"/> under
    /// <c>{baseUrl}/chat/completions</c> — the usual <c>…/v1</c> root</item>
    /// </list>
    /// The suffix is matched on the whole normalized string, so a host <i>named</i>
    /// <c>responses.example.com</c> (no path) correctly falls through to the append branch. A
    /// BaseUrl carrying a query string (Azure-OpenAI style <c>?api-version=…</c>) matches no suffix
    /// and lands in the append branch as well — unsupported, exactly as before.
    /// </summary>
    public static LlmEndpointTarget ResolveEndpoint(string? baseUrl)
    {
        var url = NormalizeAndValidateBaseUrl(baseUrl);

        if (url.EndsWith(ResponsesSuffix, StringComparison.OrdinalIgnoreCase))
            return new LlmEndpointTarget(url, url[..^ResponsesSuffix.Length], LlmApiFlavor.Responses);

        if (url.EndsWith(ChatCompletionsSuffix, StringComparison.OrdinalIgnoreCase))
            return new LlmEndpointTarget(url, url[..^ChatCompletionsSuffix.Length], LlmApiFlavor.ChatCompletions);

        return new LlmEndpointTarget(url + ChatCompletionsSuffix, url, LlmApiFlavor.ChatCompletions);
    }

    /// <summary>
    /// Parses/validates <paramref name="baseUrl"/> and returns it normalized (trailing slash
    /// trimmed). Throws <see cref="LlmException"/> (<see cref="LlmErrorKind.Unreachable"/>) on an
    /// empty, non-absolute, non-http(s) or cloud-metadata endpoint.
    /// </summary>
    public static string NormalizeAndValidateBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new LlmException(LlmErrorKind.Unreachable, "LLM baseUrl is not configured.");

        var trimmed = baseUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new LlmException(LlmErrorKind.Unreachable,
                $"LLM baseUrl must be an absolute http/https URL ('{trimmed}').");
        }

        if (IsCloudMetadataEndpoint(trimmed))
        {
            throw new LlmException(LlmErrorKind.Unreachable,
                $"SECURITY: LLM baseUrl ('{trimmed}') points at a cloud-metadata endpoint and is blocked "
                + "(169.254.0.0/16, metadata.google.internal, metadata.azure.com).");
        }

        // Local model servers such as Ollama commonly expose HTTP on loopback. Keep that useful
        // deployment mode, but never send prompts or bearer keys over cleartext to a remote host.
        // The host test is deliberately literal: no DNS lookup means a hostname cannot be rebound
        // from loopback to a remote address after validation.
        if (uri.Scheme == Uri.UriSchemeHttp && !IsLiteralLoopbackEndpoint(uri))
        {
            throw new LlmException(LlmErrorKind.Unreachable,
                $"SECURITY: LLM baseUrl ('{trimmed}') uses plaintext HTTP for a non-loopback host. "
                + "Use HTTPS; HTTP is allowed only for literal localhost/loopback endpoints.");
        }

        return trimmed.TrimEnd('/');
    }

    /// <summary>
    /// True only for a URI whose host is the exact <c>localhost</c> label or a literal loopback
    /// address. Hostnames which merely resolve to loopback are intentionally excluded: the
    /// cleartext exception must not acquire DNS-rebinding semantics.
    /// </summary>
    public static bool IsLiteralLoopbackEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        var host = endpoint.Host.Trim('[', ']');
        if (!IPAddress.TryParse(host, out var address))
            return false;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }

    /// <summary>
    /// Detects the typical cloud-provider metadata endpoints at the BaseUrl level: AWS/Azure IMDS
    /// via 169.254.169.254, GCP via metadata.google.internal, and Azure also under
    /// metadata.azure.com. Literal match only — resolving DNS on the startup path would be too
    /// slow, and hostnames like <c>api.openai.com</c> shouldn't need a DNS lookup on every boot.
    /// The DNS-rebinding case (a hostname that only resolves to a metadata IP at connect time) is
    /// covered separately by <c>LlmConnectGuard</c>.
    /// </summary>
    public static bool IsCloudMetadataEndpoint(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host;
        if (host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase)
            || host.Equals("metadata.azure.com", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = ip.GetAddressBytes();
                return bytes[0] == 169 && bytes[1] == 254; // 169.254/16 incl. cloud metadata
            }
        }

        return false;
    }
}
