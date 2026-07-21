using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Interfaces;

namespace NodePilot.Data.Security;

/// <summary>
/// Builds an <see cref="ISecretProtector"/> from a configuration snapshot — outside of
/// the dependency-injection container. Needed by the encrypting JSON configuration
/// provider, which has to decrypt <c>enc:v1:</c>-prefixed values during
/// <c>IConfiguration</c> load — at that point the <c>ServiceProvider</c> doesn't exist
/// yet and we can't pull the protector via DI.
///
/// <para>The factory deliberately handles ONLY the active provider (DPAPI / AES-GCM).
/// The migrating-fallback wrapper used by the regular DI registration is a runtime
/// feature for credential rotation; the configuration-load path needs decryption only
/// against the currently active key.</para>
///
/// <para>Validation behaviour is identical to <see cref="SecretProtectorRegistry"/>:
/// unknown provider names throw, <c>Cluster:Enabled=true</c> + DPAPI throws, missing
/// AES-GCM master key throws. The point is: the encrypting JSON provider must NEVER
/// silently fall back — a misconfigured override file with encrypted secrets that
/// "decrypt to gibberish" is a much worse failure than refusing to boot.</para>
/// </summary>
public static class SecretProtectorBootstrapFactory
{
    /// <summary>
    /// Build an <see cref="ISecretProtector"/> from the supplied configuration snapshot.
    /// The snapshot must already contain the <c>Secrets:*</c> / <c>Credentials:DpapiScope</c>
    /// / <c>Cluster:Enabled</c> keys — typically a builder loading appsettings + env-specific
    /// JSON + EnvVars + CLI (everything EXCEPT the runtime-overrides file the protector
    /// will subsequently decrypt).
    /// </summary>
    public static ISecretProtector FromConfigSnapshot(IConfiguration snapshot, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var providerName = (snapshot["Secrets:Provider"] ?? "Dpapi").Trim();
        var clusterEnabled = bool.TryParse(snapshot["Cluster:Enabled"], out var v) && v;

        var isDpapi = string.IsNullOrEmpty(providerName)
            || string.Equals(providerName, "Dpapi", StringComparison.OrdinalIgnoreCase);
        var isAesGcm = string.Equals(providerName, "AesGcm", StringComparison.OrdinalIgnoreCase);

        if (!isDpapi && !isAesGcm)
        {
            throw new InvalidOperationException(
                $"Secrets:Provider has unknown value '{providerName}'. " +
                "Allowed: 'Dpapi' (default) or 'AesGcm'. Refusing to fall back to DPAPI silently — " +
                "a typo here would otherwise produce a host-bound deployment that breaks on first " +
                "failover or DB-restore-to-different-host.");
        }

        if (clusterEnabled && isDpapi)
        {
            throw new InvalidOperationException(
                "Cluster:Enabled=true is incompatible with Secrets:Provider=Dpapi. " +
                "DPAPI ciphertexts are bound to the encrypting host; a standby node cannot decrypt " +
                "them after failover. Switch to Secrets:Provider=AesGcm and provide Secrets:MasterKey " +
                "via the Secrets__MasterKey env var on every cluster member.");
        }

        if (isAesGcm)
        {
            var key = AesGcmSecretProtector.DecodeMasterKey(
                ReadAesGcmMasterKeyMaterial(snapshot, "Secrets:"));
            log?.LogDebug("SecretProtectorBootstrapFactory selected AES-GCM provider.");
            return new AesGcmSecretProtector(key);
        }

        var dpapiScope = DpapiScopeResolver.FromConfig(snapshot);
        log?.LogDebug("SecretProtectorBootstrapFactory selected DPAPI provider (scope: {Scope}).", dpapiScope);
        return new DpapiSecretProtector(dpapiScope);
    }

    public static string ReadAesGcmMasterKeyMaterial(IConfiguration configuration, string prefix)
    {
        var fileKey = $"{prefix}MasterKeyFile";
        var keyFile = configuration[fileKey];
        if (!string.IsNullOrWhiteSpace(keyFile))
        {
            var expanded = Environment.ExpandEnvironmentVariables(keyFile);
            if (!File.Exists(expanded))
                throw new InvalidOperationException($"{fileKey} points to a file that does not exist: {expanded}");

            var content = File.ReadAllText(expanded).Trim();
            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidOperationException($"{fileKey} points to an empty file.");
            return content;
        }

        var key = configuration[$"{prefix}MasterKey"];
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                $"{prefix}MasterKey or {fileKey} is required when {prefix}Provider=AesGcm.");

        return key;
    }
}
