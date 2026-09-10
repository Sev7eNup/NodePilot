using NodePilot.Core.Clients;
using Spectre.Console;

namespace NodePilot.Cli.Output;

/// <summary>
/// Warnings about a relaxed TLS posture. They go to stderr on every affected call: a bypass or a
/// pin that overrode hostname validation must never be silent.
/// </summary>
public static class TlsNotices
{
    public static void WriteBefore(OutputWriter writer, ClientTlsOptions tls, string? pinNotice)
    {
        if (!string.IsNullOrEmpty(pinNotice)) writer.Warning(Markup.Escape(pinNotice));
        if (tls.SkipVerification)
        {
            writer.Warning(
                "TLS-Prüfung deaktiviert (--insecure-tls) — die Verbindung ist nicht authentifiziert.");
        }
    }

    public static void WriteAfter(OutputWriter writer, PresentedCertificateInfo? observation)
    {
        if (observation is not { AcceptedDespiteNameMismatch: true }) return;
        var names = observation.DnsNames.Count == 0 ? "keine" : string.Join(", ", observation.DnsNames);
        writer.Warning(Markup.Escape(
            $"Pin akzeptiert — die Zertifikatsnamen passen nicht zum Host '{observation.RequestHost}' "
            + $"(DNS: {names}). Nur für Lab/Pilot."));
    }
}
