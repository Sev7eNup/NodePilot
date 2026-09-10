using System.Net.Security;

namespace NodePilot.Core.Clients;

/// <summary>
/// Immutable snapshot of the certificate a server presented during one handshake, plus how it was
/// judged. The fields are copied inside the validation callback because the certificate object
/// belongs to the SslStream and is disposed with it.
/// </summary>
public sealed record PresentedCertificateInfo
{
    public required string RequestHost { get; init; }

    /// <summary>Port that was tried. Descriptive only; it never takes part in the trust decision.</summary>
    public int RequestPort { get; init; }

    public string Subject { get; init; } = "";
    public string Issuer { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public IReadOnlyList<string> DnsNames { get; init; } = Array.Empty<string>();
    public DateTimeOffset? NotBefore { get; init; }
    public DateTimeOffset? NotAfter { get; init; }
    public SslPolicyErrors PolicyErrors { get; init; }
    public string ChainStatus { get; init; } = "";
    public bool PinConfigured { get; init; }
    public bool PinMatched { get; init; }
    public bool AcceptedDespiteNameMismatch { get; init; }
    public bool AcceptedWithoutVerification { get; init; }

    /// <summary>False when the server presented no certificate at all.</summary>
    public bool HasCertificate => Sha256.Length > 0;

    public bool IsSelfSigned => Subject.Length > 0 && string.Equals(Subject, Issuer, StringComparison.Ordinal);

    public bool IsExpired
        => (NotAfter.HasValue && NotAfter.Value < DateTimeOffset.UtcNow)
           || (NotBefore.HasValue && NotBefore.Value > DateTimeOffset.UtcNow);

    /// <summary>
    /// URL that reaches this server under a name the certificate actually carries, or null when it
    /// carries none that a client could dial. This is the fix for a name mismatch, so the clients
    /// can offer it instead of leaving the operator to assemble it from the name list.
    /// </summary>
    public string? SuggestedServerUrl
    {
        get
        {
            var usable = DnsNames.Where(n => !string.IsNullOrWhiteSpace(n) && !n.Contains('*')).ToList();
            if (usable.Count == 0) return null;

            // A routable name works from anywhere; localhost only works on the server itself, and
            // the observation cannot tell where the client runs.
            var host = usable.FirstOrDefault(n => !string.Equals(n, "localhost", StringComparison.OrdinalIgnoreCase))
                       ?? usable[0];
            var port = RequestPort is 443 or <= 0 ? "" : $":{RequestPort}";
            return $"https://{host}{port}";
        }
    }
}
