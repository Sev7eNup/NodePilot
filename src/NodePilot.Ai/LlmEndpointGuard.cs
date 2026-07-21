using System.Net;
using System.Net.Sockets;

namespace NodePilot.Ai;

/// <summary>
/// Single validation point for an LLM endpoint <c>BaseUrl</c> — whether it comes from the global
/// <c>Llm:*</c> config or a per-node <see cref="LlmConnection"/> override. Enforces an absolute
/// http/https URL and rejects cloud-metadata endpoints. Used by <see cref="ILlmClientFactory"/>,
/// the <c>llmQuery</c> activity, the settings boot-validator and the settings test-probe so there
/// is no unguarded LLM-egress path. The complementary TCP-connect-time DNS-rebinding guard lives
/// in <c>LlmConnectGuard</c> (the <see cref="System.Net.Http.SocketsHttpHandler"/> ConnectCallback).
/// </summary>
public static class LlmEndpointGuard
{
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

        return trimmed.TrimEnd('/');
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
