using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NodePilot.Core.Clients;

/// <summary>How a raw pin string was understood by <see cref="CertificatePin.Parse"/>.</summary>
public enum CertificatePinFormat
{
    /// <summary>Parsed into a canonical SHA-256 fingerprint.</summary>
    Valid,

    /// <summary>No value was supplied.</summary>
    Empty,

    /// <summary>40 hex characters — the SHA-1 thumbprint Windows shows for a certificate.</summary>
    Sha1Thumbprint,

    /// <summary>Neither, so the value cannot identify a certificate.</summary>
    Malformed,
}

/// <summary>
/// SHA-256 certificate fingerprints used by the HTTP-only clients to pin a NodePilot server
/// certificate. The canonical form is 64 uppercase hex characters without separators — the same
/// shape the desktop shell stores as <c>certificateSha256</c>. Errors are returned as a format
/// enum rather than text because the CLI renders German and the MCP server English.
/// </summary>
public static class CertificatePin
{
    /// <summary>Length of a canonical pin: SHA-256 as hex.</summary>
    public const int HexLength = 64;

    private const int Sha1HexLength = 40;

    /// <summary>
    /// Normalizes user input: case-insensitive, optional <c>:</c>/<c>-</c>/space separators and an
    /// optional <c>sha256:</c> prefix are accepted.
    /// </summary>
    public static CertificatePinFormat Parse(string? raw, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(raw)) return CertificatePinFormat.Empty;

        var value = raw.Trim();
        if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            value = value[7..];

        Span<char> buffer = stackalloc char[HexLength];
        var length = 0;
        foreach (var c in value)
        {
            if (c is ':' or '-' or ' ') continue;
            if (!Uri.IsHexDigit(c)) return CertificatePinFormat.Malformed;
            if (length == HexLength)
            {
                // Longer than SHA-256 and still hex: not a fingerprint we can use.
                return CertificatePinFormat.Malformed;
            }

            buffer[length++] = char.ToUpperInvariant(c);
        }

        if (length == HexLength)
        {
            normalized = new string(buffer);
            return CertificatePinFormat.Valid;
        }

        return length == Sha1HexLength ? CertificatePinFormat.Sha1Thumbprint : CertificatePinFormat.Malformed;
    }

    /// <summary>True when the value is a usable pin; <paramref name="normalized"/> is canonical.</summary>
    public static bool TryParse(string? raw, out string normalized)
        => Parse(raw, out normalized) == CertificatePinFormat.Valid;

    /// <summary>Canonical fingerprint of a certificate, in the same shape <see cref="Parse"/> emits.</summary>
    public static string Compute(X509Certificate2 certificate)
        => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    /// <summary>Constant-shape comparison of two canonical fingerprints.</summary>
    public static bool Matches(string? pin, string? fingerprint)
        => !string.IsNullOrEmpty(pin)
           && !string.IsNullOrEmpty(fingerprint)
           && string.Equals(pin, fingerprint, StringComparison.OrdinalIgnoreCase);
}
