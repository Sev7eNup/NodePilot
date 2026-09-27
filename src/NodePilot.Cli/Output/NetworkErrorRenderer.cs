using System.Text;
using NodePilot.Core.Clients;

namespace NodePilot.Cli.Output;

/// <summary>
/// Renders a failed request as plain text: the unwrapped cause chain, the certificate the server
/// presented and how to proceed. Plain text on purpose — certificate subjects contain characters
/// that break Spectre markup, so the whole block is escaped once by
/// <see cref="OutputWriter.ErrorBlock"/>.
/// </summary>
public static class NetworkErrorRenderer
{
    public static string Render(Exception exception, string? server = null)
    {
        var info = NetworkFailureAnalyzer.Analyze(exception);
        var text = new StringBuilder();
        var target = string.IsNullOrWhiteSpace(server) ? "" : $" to {server}";

        text.Append("Network error: ");
        text.AppendLine(info.IsTls
            ? $"Could not establish a TLS connection{target}."
            : $"Connection{target} failed.");

        var cause = DescribeCause(info);
        if (cause is not null) text.AppendLine($"  Cause: {cause}");
        if (info.HasNameMismatch && info.Tls != TlsFailureKind.NameMismatch)
            text.AppendLine($"         Also: {NameMismatchCause(info)}");
        foreach (var line in info.CauseChain) text.AppendLine($"  → {line}");

        var certificate = info.Certificate;
        if (certificate is { HasCertificate: true })
        {
            var kind = certificate.IsSelfSigned ? " (self-signed)" : "";
            text.AppendLine($"  Certificate: {certificate.Subject}{kind}");
            if (certificate.DnsNames.Count > 0)
                text.AppendLine($"  DNS names: {string.Join(", ", certificate.DnsNames)}");
            if (certificate.NotAfter.HasValue)
                text.AppendLine($"  Valid until: {certificate.NotAfter.Value.UtcDateTime:yyyy-MM-dd} (UTC)");
            text.AppendLine($"  SHA-256: {certificate.Sha256}");
        }

        AppendRemedies(text, info);
        return text.ToString().TrimEnd();
    }

    private static string? DescribeCause(NetworkFailureInfo info) => info.Tls switch
    {
        TlsFailureKind.PinMismatch =>
            "The configured TLS pin does not match the certificate the server presented.",
        TlsFailureKind.UntrustedChain =>
            $"This client does not trust the server certificate{ChainSuffix(info)}.",
        TlsFailureKind.NameMismatch => NameMismatchCause(info),
        TlsFailureKind.Expired => "The server certificate has expired or is not valid yet.",
        TlsFailureKind.NoCertificate => "The server did not present a certificate.",
        TlsFailureKind.ProtocolOrCipher =>
            "The TLS handshake failed before the certificate was checked (protocol or cipher).",
        _ => null,
    };

    private static string NameMismatchCause(NetworkFailureInfo info)
        => $"The hostname is not among the certificate names{DnsSuffix(info)}.";

    private static string ChainSuffix(NetworkFailureInfo info)
        => string.IsNullOrEmpty(info.Certificate?.ChainStatus) ? "" : $" ({info.Certificate.ChainStatus})";

    private static string DnsSuffix(NetworkFailureInfo info)
    {
        var names = info.Certificate?.DnsNames ?? Array.Empty<string>();
        return names.Count == 0 ? "" : $" (DNS: {string.Join(", ", names)})";
    }

    /// <summary>
    /// Collects the remedies that address what was actually diagnosed. A remedy for a cause the
    /// server does not have sends the operator down the wrong path — a root import fixes neither a
    /// name mismatch nor an expired certificate.
    /// </summary>
    private static void AppendRemedies(StringBuilder text, NetworkFailureInfo info)
    {
        if (info.Tls == TlsFailureKind.PinMismatch)
        {
            // Never suggest pinning what was just seen, or bypassing: a wrong pin means the
            // certificate changed, and that is the one case worth looking at before proceeding.
            text.AppendLine("  Fix: Check the certificate. If the change is intended, set the new pin with");
            text.AppendLine("       `np config set tls-thumbprint <SHA-256>`.");
            return;
        }

        var certificate = info.Certificate;
        if (certificate is not { HasCertificate: true }) return;

        var remedies = new List<string>();
        var combined = info.HasNameMismatch && info.Tls != TlsFailureKind.NameMismatch;

        if (info.HasNameMismatch)
        {
            if (certificate.SuggestedServerUrl is { } url)
            {
                remedies.Add(combined
                    ? $"np config set server {url}   (the host must be named in the certificate)"
                    : $"np config set server {url}");
                if (!combined)
                    remedies.Add("The host must be one of the certificate names; importing the root does not change that.");
            }
            else
            {
                remedies.Add("The certificate names no usable hostname. Reissue it with the name");
                remedies.Add("the server is reached under.");
            }
        }

        switch (info.Tls)
        {
            case TlsFailureKind.UntrustedChain or TlsFailureKind.Unknown:
                remedies.Add("np auth login --tls-thumbprint <SHA-256 above>   (stored in the profile)");
                remedies.Add(@"or import the certificate into Cert:\LocalMachine\Root (system-wide)");
                break;
            case TlsFailureKind.Expired:
                remedies.Add("Renew the certificate. Importing the root does not help with an expired certificate.");
                break;
            case TlsFailureKind.NameMismatch when certificate.SuggestedServerUrl is not null:
                // A pin also gets past a name mismatch, and an alias or reverse-proxy host is a
                // legitimate reason to keep the URL as it is.
                remedies.Add("If the host has to stay (alias, reverse proxy): np auth login --tls-thumbprint <SHA-256 above>");
                break;
        }

        remedies.Add("once, without verification: --insecure-tls");

        text.AppendLine($"  Fix: {remedies[0]}");
        foreach (var line in remedies.Skip(1)) text.AppendLine($"       {line}");
    }
}
