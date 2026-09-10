namespace NodePilot.Core.Clients;

/// <summary>
/// Ties a TLS handshake observation to the request that caused it. The validation callback runs
/// deep inside the primary handler, so the observation is parked in an <see cref="AsyncLocal{T}"/>
/// attempt that only flows to the request currently being sent, and is attached to the thrown
/// exception on failure. A process-wide "last observation" slot cannot do this: a successful
/// handshake followed by a later failure to the same host, or two concurrent MCP calls, would
/// report the wrong certificate.
/// </summary>
public sealed class TlsObservationHandler : DelegatingHandler
{
    /// <summary><see cref="Exception.Data"/> key carrying a <see cref="PresentedCertificateInfo"/>.</summary>
    public const string ExceptionDataKey = "NodePilot.Tls";

    private static readonly AsyncLocal<TlsAttempt?> CurrentAttempt = new();

    public TlsObservationHandler(HttpMessageHandler innerHandler) : base(innerHandler) { }

    /// <summary>
    /// Called from the certificate validation callback. Does nothing when no attempt is in flow —
    /// then the handshake belongs to a pooled connection someone else opened, and the caller must
    /// report the failure without a certificate rather than with a foreign one.
    /// </summary>
    public static void Record(PresentedCertificateInfo observation)
    {
        var attempt = CurrentAttempt.Value;
        if (attempt is not null) attempt.Observation = observation;
    }

    /// <summary>Reads the observation a failed request collected, if any.</summary>
    public static PresentedCertificateInfo? ObservationOf(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Data[ExceptionDataKey] is PresentedCertificateInfo observation) return observation;
        }

        return null;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var attempt = new TlsAttempt();
        CurrentAttempt.Value = attempt;
        try
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (attempt.Observation is not null)
        {
            ex.Data[ExceptionDataKey] = attempt.Observation;
            throw;
        }
        finally
        {
            CurrentAttempt.Value = null;
        }
    }

    private sealed class TlsAttempt
    {
        public PresentedCertificateInfo? Observation { get; set; }
    }
}
