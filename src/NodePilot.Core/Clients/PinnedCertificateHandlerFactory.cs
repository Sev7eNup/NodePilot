using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NodePilot.Core.Clients;

/// <summary>
/// Builds the message handler both HTTP-only clients use: stock chain validation, plus an optional
/// SHA-256 pin for a lab certificate the client machine does not trust, plus an optional bypass.
/// Every outcome is recorded so a failure can name the certificate the server actually presented.
/// </summary>
public static class PinnedCertificateHandlerFactory
{
    /// <summary>
    /// Wraps <paramref name="primary"/> (or a fresh <see cref="HttpClientHandler"/>) in the
    /// validation callback and the observation handler. Callers that need handler properties —
    /// Windows SSO sets credentials and a cookie container — configure the primary first and pass
    /// it in.
    /// </summary>
    /// <param name="observer">
    /// Optional per-client sink for handshake observations, so a command can warn about how its own
    /// connection was accepted. Scoped to the client that was built here — there is no shared slot.
    /// </param>
    public static HttpMessageHandler Create(
        ClientTlsOptions? options,
        HttpClientHandler? primary = null,
        Action<PresentedCertificateInfo>? observer = null)
    {
        var tls = options ?? ClientTlsOptions.None;
        var handler = primary ?? new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) =>
        {
            var accepted = Evaluate(
                tls, request.RequestUri?.IdnHost ?? "", request.RequestUri?.Port ?? 0,
                certificate, chain, errors, out var observation);
            TlsObservationHandler.Record(observation);
            observer?.Invoke(observation);
            return accepted;
        };

        return new TlsObservationHandler(handler);
    }

    /// <summary>
    /// The whole trust decision, as a pure function so it can be tested without a handshake.
    /// Order matters: a valid chain wins, then a matching pin, and a configured pin that does not
    /// match is fatal even under <see cref="ClientTlsOptions.SkipVerification"/> — a wrong pin is
    /// an alarm, not a formality.
    /// </summary>
    public static bool Evaluate(
        ClientTlsOptions options,
        string requestHost,
        int requestPort,
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors errors,
        out PresentedCertificateInfo observation)
    {
        var fingerprint = certificate is null ? "" : CertificatePin.Compute(certificate);
        var pinMatched = options.HasPin && CertificatePin.Matches(options.PinnedSha256, fingerprint);

        bool accepted;
        if (errors == SslPolicyErrors.None) accepted = true;
        else if (options.HasPin) accepted = pinMatched;
        else accepted = options.SkipVerification;

        observation = Describe(requestHost, requestPort, certificate, chain, errors, fingerprint) with
        {
            PinConfigured = options.HasPin,
            PinMatched = pinMatched,
            AcceptedDespiteNameMismatch =
                accepted && pinMatched && errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch),
            AcceptedWithoutVerification = accepted && !pinMatched && errors != SslPolicyErrors.None,
        };
        return accepted;
    }

    private static PresentedCertificateInfo Describe(
        string requestHost,
        int requestPort,
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors errors,
        string fingerprint)
    {
        if (certificate is null)
        {
            return new PresentedCertificateInfo
            {
                RequestHost = requestHost, RequestPort = requestPort, PolicyErrors = errors,
            };
        }

        return new PresentedCertificateInfo
        {
            RequestHost = requestHost,
            RequestPort = requestPort,
            Subject = certificate.Subject,
            Issuer = certificate.Issuer,
            Sha256 = fingerprint,
            DnsNames = ReadDnsNames(certificate),
            NotBefore = certificate.NotBefore.ToUniversalTime(),
            NotAfter = certificate.NotAfter.ToUniversalTime(),
            PolicyErrors = errors,
            ChainStatus = SummarizeChain(chain),
        };
    }

    private static IReadOnlyList<string> ReadDnsNames(X509Certificate2 certificate)
    {
        foreach (var extension in certificate.Extensions)
        {
            if (extension.Oid?.Value != "2.5.29.17") continue;
            try
            {
                var san = new X509SubjectAlternativeNameExtension(extension.RawData, extension.Critical);
                return san.EnumerateDnsNames().ToList();
            }
            catch (CryptographicException)
            {
                // A malformed SAN must not turn a diagnostic into a second failure.
                return Array.Empty<string>();
            }
        }

        return Array.Empty<string>();
    }

    private static string SummarizeChain(X509Chain? chain)
    {
        if (chain is null || chain.ChainStatus.Length == 0) return "";
        return string.Join(
            ", ",
            chain.ChainStatus.Select(s => s.Status.ToString()).Distinct(StringComparer.Ordinal));
    }
}
