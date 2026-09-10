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
        if (info.HasNameMismatch && info.Tls != TlsFailureKind.NameMismatch)
            text.AppendLine($"           Zusätzlich: {NameMismatchCause(info)}");
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
        TlsFailureKind.NameMismatch => NameMismatchCause(info),
        TlsFailureKind.Expired => "Das Serverzertifikat ist abgelaufen oder noch nicht gültig.",
        TlsFailureKind.NoCertificate => "Der Server hat kein Zertifikat präsentiert.",
        TlsFailureKind.ProtocolOrCipher =>
            "Der TLS-Handshake scheiterte vor der Zertifikatsprüfung (Protokoll oder Cipher).",
        _ => null,
    };

    private static string NameMismatchCause(NetworkFailureInfo info)
        => $"Der Hostname steht nicht in den Zertifikatsnamen{DnsSuffix(info)}.";

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
            text.AppendLine("  Abhilfe: Zertifikat prüfen. Ist der Wechsel gewollt, den Pin mit");
            text.AppendLine("           `np config set tls-thumbprint <SHA-256>` neu setzen.");
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
                    ? $"np config set server {url}   (Host muss im Zertifikat stehen)"
                    : $"np config set server {url}");
                if (!combined)
                    remedies.Add("Der Host muss ein Name aus dem Zertifikat sein; ein Root-Import ändert daran nichts.");
            }
            else
            {
                remedies.Add("Das Zertifikat nennt keinen verwendbaren Hostnamen — neu ausstellen mit dem Namen,");
                remedies.Add("unter dem der Server erreichbar ist.");
            }
        }

        switch (info.Tls)
        {
            case TlsFailureKind.UntrustedChain or TlsFailureKind.Unknown:
                remedies.Add("np auth login --tls-thumbprint <SHA-256 oben>   (dauerhaft im Profil)");
                remedies.Add(@"oder Zertifikat nach Cert:\LocalMachine\Root importieren (systemweit)");
                break;
            case TlsFailureKind.Expired:
                remedies.Add("Zertifikat erneuern — ein Root-Import hilft bei einem abgelaufenen Zertifikat nicht.");
                break;
            case TlsFailureKind.NameMismatch when certificate.SuggestedServerUrl is not null:
                // A pin also gets past a name mismatch, and an alias or reverse-proxy host is a
                // legitimate reason to keep the URL as it is.
                remedies.Add("Muss der Host so bleiben (Alias, Reverse-Proxy): np auth login --tls-thumbprint <SHA-256 oben>");
                break;
        }

        remedies.Add("einmalig ohne Prüfung: --insecure-tls");

        text.AppendLine($"  Abhilfe: {remedies[0]}");
        foreach (var line in remedies.Skip(1)) text.AppendLine($"           {line}");
    }
}
