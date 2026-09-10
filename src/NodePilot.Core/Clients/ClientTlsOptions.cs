namespace NodePilot.Core.Clients;

/// <summary>
/// How an HTTP-only client treats the server certificate. Both knobs exist for lab servers whose
/// certificate is not trusted by the client machine.
/// </summary>
/// <param name="PinnedSha256">
/// Canonical SHA-256 fingerprint that is accepted in addition to a valid chain. The pin is
/// additive, not restrictive: a certificate that validates normally is accepted even when it does
/// not match, so a certificate renewal never breaks a correctly trusted server. A configured pin
/// that does not match is fatal — <see cref="SkipVerification"/> does not override it.
/// </param>
/// <param name="SkipVerification">
/// Accept any certificate. Emergency switch for one-off calls; never persisted, and every call
/// that uses it prints a warning.
/// </param>
public sealed record ClientTlsOptions(string? PinnedSha256 = null, bool SkipVerification = false)
{
    /// <summary>Stock validation: trusted chain or nothing.</summary>
    public static readonly ClientTlsOptions None = new();

    public bool HasPin => !string.IsNullOrEmpty(PinnedSha256);
}
