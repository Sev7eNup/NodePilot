namespace NodePilot.Api.Configuration.Validators;

/// <summary>
/// Pre-flight check for <c>Secrets:Provider</c> consistency. The
/// <see cref="NodePilot.Data.Security.SecretProtectorBootstrapFactory"/> already
/// throws on the same conditions during configuration load (it has to — the
/// encrypting JSON provider needs the protector built before DI exists), but
/// duplicating the rule here as an <see cref="IBootValidator"/> serves two
/// distinct callers:
///
/// <list type="bullet">
///   <item><b>Save-side reuse:</b> The Admin Settings API runs every validator
///   against the simulated post-save config so a write that would brick the next
///   boot is rejected with 400 instead of silently persisted.</item>
///   <item><b>Aggregated boot errors:</b> The factory fails on the first conflict;
///   this validator collects all secrets-related issues so the operator can fix
///   them in a single restart cycle.</item>
/// </list>
/// </summary>
public sealed class SecretsConsistencyBootValidator : IBootValidator
{
    private static readonly string[] KnownProviders = ["Dpapi", "AesGcm"];

    public string Name => "SecretsConsistency";

    public void Validate(IConfiguration configuration, IList<BootValidationIssue> issues)
    {
        var providerName = (configuration["Secrets:Provider"] ?? "Dpapi").Trim();
        var clusterEnabled = bool.TryParse(configuration["Cluster:Enabled"], out var v) && v;

        var isDpapi = string.IsNullOrEmpty(providerName)
            || string.Equals(providerName, "Dpapi", StringComparison.OrdinalIgnoreCase);
        var isAesGcm = string.Equals(providerName, "AesGcm", StringComparison.OrdinalIgnoreCase);

        if (!isDpapi && !isAesGcm)
        {
            issues.Add(new BootValidationIssue(
                Name, BootValidationSeverity.Error, "Secrets:Provider",
                $"Unknown value '{providerName}'. Allowed: {string.Join(", ", KnownProviders)}. " +
                "Silent fall-back to DPAPI on a typo would produce a host-bound deployment that breaks " +
                "on first failover or DB-restore-to-different-host."));
            // No point checking the rest — the provider name is wrong.
            return;
        }

        if (clusterEnabled && isDpapi)
        {
            issues.Add(new BootValidationIssue(
                Name, BootValidationSeverity.Error, "Secrets:Provider",
                "Cluster:Enabled=true is incompatible with Secrets:Provider=Dpapi. " +
                "DPAPI ciphertexts are bound to the encrypting host; a standby node cannot decrypt " +
                "them after failover. Switch to AesGcm and provide Secrets:MasterKey via the " +
                "Secrets__MasterKey env var on every cluster member."));
        }

        if (isAesGcm
            && string.IsNullOrWhiteSpace(configuration["Secrets:MasterKey"])
            && string.IsNullOrWhiteSpace(configuration["Secrets:MasterKeyFile"]))
        {
            issues.Add(new BootValidationIssue(
                Name, BootValidationSeverity.Error, "Secrets:MasterKey",
                "Secrets:Provider=AesGcm requires a base64-encoded 32-byte master key in Secrets:MasterKey or Secrets:MasterKeyFile. " +
                "Generate one with a cryptographic RNG: `$r=[Security.Cryptography.RandomNumberGenerator]::Create();" +
                "$b=New-Object byte[] 32;try{$r.GetBytes($b);[Convert]::ToBase64String($b)}finally{$r.Dispose();[Array]::Clear($b,0,$b.Length)}` " +
                "and ship it via env var or an ACL-restricted sidecar file on every node."));
        }
    }
}
