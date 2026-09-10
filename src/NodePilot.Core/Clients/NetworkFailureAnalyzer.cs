using System.Net.Security;
using System.Security.Authentication;

namespace NodePilot.Core.Clients;

/// <summary>What kind of TLS problem a failed request ran into.</summary>
public enum TlsFailureKind
{
    /// <summary>Not a TLS failure (DNS, refused connection, timeout, HTTP-level error).</summary>
    None,
    UntrustedChain,
    NameMismatch,
    PinMismatch,
    Expired,
    NoCertificate,

    /// <summary>The handshake failed before certificate validation — no common protocol or cipher.</summary>
    ProtocolOrCipher,
    Unknown,
}

/// <summary>Language-neutral analysis of a failed request; the clients render it themselves.</summary>
public sealed record NetworkFailureInfo(
    IReadOnlyList<string> CauseChain,
    TlsFailureKind Tls,
    PresentedCertificateInfo? Certificate)
{
    public bool IsTls => Tls != TlsFailureKind.None;
}

/// <summary>
/// Unwraps the exception chain a failed HTTP call throws. Without this the clients only ever show
/// the outermost message, which for a handshake failure is the useless "The SSL connection could
/// not be established, see inner exception."
/// </summary>
public static class NetworkFailureAnalyzer
{
    public static NetworkFailureInfo Analyze(Exception exception)
    {
        var causes = new List<string>();
        var handshakeFailed = false;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException) handshakeFailed = true;
            var message = current.Message.Trim();
            if (message.Length > 0 && (causes.Count == 0 || causes[^1] != message)) causes.Add(message);
        }

        var certificate = TlsObservationHandler.ObservationOf(exception);
        return new NetworkFailureInfo(causes, Classify(certificate, handshakeFailed), certificate);
    }

    private static TlsFailureKind Classify(PresentedCertificateInfo? certificate, bool handshakeFailed)
    {
        if (certificate is null) return handshakeFailed ? TlsFailureKind.ProtocolOrCipher : TlsFailureKind.None;
        if (certificate.PinConfigured && !certificate.PinMatched) return TlsFailureKind.PinMismatch;
        if (!certificate.HasCertificate) return TlsFailureKind.NoCertificate;
        if (certificate.IsExpired) return TlsFailureKind.Expired;
        if (certificate.PolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
            return TlsFailureKind.UntrustedChain;
        if (certificate.PolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
            return TlsFailureKind.NameMismatch;
        return TlsFailureKind.Unknown;
    }
}
