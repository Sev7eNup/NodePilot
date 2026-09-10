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
        var target = string.IsNullOrWhiteSpace(server) ? "" : $" zu {server}";

        text.Append("Netzwerk-Fehler: ");
        text.AppendLine(info.IsTls
            ? $"TLS-Verbindung{target} nicht möglich."
            : $"Verbindung{target} fehlgeschlagen.");

        var cause = DescribeCause(info);
        if (cause is not null) text.AppendLine($"  Ursache: {cause}");
        foreach (var line in info.CauseChain) text.AppendLine($"  → {line}");

        var certificate = info.Certificate;
        if (certificate is { HasCertificate: true })
        {
            var kind = certificate.IsSelfSigned ? " (selbstsigniert)" : "";
            text.AppendLine($"  Zertifikat: {certificate.Subject}{kind}");
            if (certificate.DnsNames.Count > 0)
                text.AppendLine($"  DNS-Namen: {string.Join(", ", certificate.DnsNames)}");
            if (certificate.NotAfter.HasValue)
                text.AppendLine($"  Gültig bis: {certificate.NotAfter.Value.UtcDateTime:yyyy-MM-dd} (UTC)");
            text.AppendLine($"  SHA-256: {certificate.Sha256}");
        }

        AppendRemedies(text, info);
        return text.ToString().TrimEnd();
    }

    private static string? DescribeCause(NetworkFailureInfo info) => info.Tls switch
    {
        TlsFailureKind.PinMismatch =>
            "Der konfigurierte TLS-Pin passt nicht zum präsentierten Zertifikat.",
        TlsFailureKind.UntrustedChain =>
            $"Serverzertifikat auf diesem Client nicht vertrauenswürdig{ChainSuffix(info)}.",
        TlsFailureKind.NameMismatch =>
            $"Der Hostname steht nicht in den Zertifikatsnamen{DnsSuffix(info)}.",
        TlsFailureKind.Expired => "Das Serverzertifikat ist abgelaufen oder noch nicht gültig.",
        TlsFailureKind.NoCertificate => "Der Server hat kein Zertifikat präsentiert.",
        TlsFailureKind.ProtocolOrCipher =>
            "Der TLS-Handshake scheiterte vor der Zertifikatsprüfung (Protokoll oder Cipher).",
        _ => null,
    };

    private static string ChainSuffix(NetworkFailureInfo info)
        => string.IsNullOrEmpty(info.Certificate?.ChainStatus) ? "" : $" ({info.Certificate.ChainStatus})";

    private static string DnsSuffix(NetworkFailureInfo info)
    {
        var names = info.Certificate?.DnsNames ?? Array.Empty<string>();
        return names.Count == 0 ? "" : $" (DNS: {string.Join(", ", names)})";
    }

    private static void AppendRemedies(StringBuilder text, NetworkFailureInfo info)
    {
        if (info.Tls == TlsFailureKind.PinMismatch)
        {
            // Never suggest pinning what was just seen, or bypassing: a wrong pin means the
            // certificate changed, and that is the one case worth looking at before proceeding.
            text.AppendLine("  Abhilfe: Zertifikat prüfen. Ist der Wechsel gewollt, den Pin mit");
            text.AppendLine("           `np config set tls-thumbprint <SHA-256>` neu setzen.");
            return;
        }

        if (info.Certificate is not { HasCertificate: true }) return;

        text.AppendLine("  Abhilfe: np auth login --tls-thumbprint <SHA-256 oben>   (dauerhaft im Profil)");
        text.AppendLine(@"           oder Zertifikat nach Cert:\LocalMachine\Root importieren (systemweit)");
        text.AppendLine("           einmalig ohne Prüfung: --insecure-tls");
    }
}
