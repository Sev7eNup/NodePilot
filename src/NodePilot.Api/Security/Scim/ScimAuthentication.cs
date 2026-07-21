using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace NodePilot.Api.Security.Scim;

public sealed class ScimAuthentication(IOptions<ScimOptions> options)
{
    public bool IsAuthorized(HttpRequest request)
    {
        var current = options.Value;
        if (!current.Enabled || current.BearerToken is not { Length: >= 32 and <= 4096 }) return false;

        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var presented = header[prefix.Length..];
        if (presented.Length is < 32 or > 4096) return false;
        var presentedBytes = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        // Evaluate both slots so the comparison path does not disclose which rotation
        // token matched. The previous slot is deliberately operator-controlled rather
        // than time-based: this keeps rotation deterministic across HA nodes.
        return Matches(current.BearerToken, presentedBytes)
               | Matches(current.PreviousBearerToken, presentedBytes);
    }

    private static bool Matches(string? expected, ReadOnlySpan<byte> presentedHash)
    {
        if (expected is not { Length: >= 32 and <= 4096 }) return false;
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(expectedHash, presentedHash);
    }
}
