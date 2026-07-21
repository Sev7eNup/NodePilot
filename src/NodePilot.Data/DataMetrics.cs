using System.Diagnostics.Metrics;
using NodePilot.Core.Telemetry;

namespace NodePilot.Data;

/// <summary>
/// Data-layer metrics (credential encrypt/decrypt observability). The meter name comes
/// from the shared Core constants registry — no NodePilot.Telemetry dependency needed,
/// and no "keep in sync" literal either.
/// </summary>
public static class DataMetrics
{
    public static readonly Meter Meter = new(TelemetryConstants.Meters.Data, "1.0.0");

    public static readonly Counter<long> CredentialCryptoCalls = Meter.CreateCounter<long>(
        "nodepilot.credential.crypto.calls", unit: "1",
        description: "DPAPI encrypt/decrypt calls, tagged by operation (encrypt/decrypt) and result (success/failure).");

    public static readonly Histogram<double> CredentialCryptoDuration = Meter.CreateHistogram<double>(
        "nodepilot.credential.crypto.duration", unit: "ms",
        description: "DPAPI encrypt/decrypt latency, tagged by operation.");

    /// <summary>
    /// Counts decrypts served by the *legacy* protector when MigratingSecretProtector is
    /// wired. Operators watch this drop to zero after a re-encrypt sweep — at that point
    /// the legacy config can be removed safely.
    /// </summary>
    public static readonly Counter<long> CredentialCryptoLegacyReads = Meter.CreateCounter<long>(
        "nodepilot.credential.crypto.legacy_reads", unit: "1",
        description: "Decrypts served by the legacy provider during a migration window. " +
                     "Should drop to zero after a successful re-encrypt sweep.");
}
