using NodePilot.Core.Clients;

namespace NodePilot.Cli.Settings;

/// <summary>
/// Turns a raw pin value from a flag, an environment variable or the config file into a canonical
/// fingerprint. A value that cannot be used is always an error — never a silently dropped pin,
/// which would downgrade a pinned connection to an unpinned one.
/// </summary>
public static class TlsPinInput
{
    private const string Sha256Recipe =
        @"To compute the SHA-256: Get-ChildItem Cert:\LocalMachine\My\<SHA1> | ForEach-Object "
        + @"{ [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($_.RawData)) -replace '-' }";

    public static string Require(string? raw, string source)
    {
        switch (CertificatePin.Parse(raw, out var normalized))
        {
            case CertificatePinFormat.Valid:
                return normalized;
            case CertificatePinFormat.Sha1Thumbprint:
                throw new InvalidOperationException(
                    source + " expects a SHA-256 fingerprint (64 hex characters), but got the "
                    + @"SHA-1 thumbprint shown in 'Cert:\LocalMachine\My'. " + Sha256Recipe);
            default:
                throw new InvalidOperationException(
                    source + " is not a SHA-256 fingerprint (expected 64 hex characters).");
        }
    }
}
