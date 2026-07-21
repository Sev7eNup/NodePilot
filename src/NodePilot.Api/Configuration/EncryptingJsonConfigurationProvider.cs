using System.Text;
using Microsoft.Extensions.Configuration.Json;
using NodePilot.Core.Interfaces;

namespace NodePilot.Api.Configuration;

/// <summary>
/// JSON configuration provider that transparently decrypts values written with the
/// <c>enc:v1:&lt;base64&gt;</c> prefix using the source's <see cref="ISecretProtector"/>.
/// Everything else is delegated to <see cref="JsonConfigurationProvider"/> — non-prefixed
/// values are passed through unchanged.
///
/// <para><b>Why this exists:</b> The Admin Settings API persists user-edited values
/// (LDAP password, SMTP password, LLM API key, …) into <c>appsettings.runtime.json</c>.
/// Storing them in plaintext would let anyone with FS-read access on the host harvest
/// every secret in one shot; storing them in a sidecar file fragments the data and
/// complicates HA-cluster file synchronisation. Encrypting in-place keeps the override
/// file a single coherent unit while ensuring an unauthorised reader sees only base64
/// blobs.</para>
///
/// <para><b>Why hard-fail on decryption errors:</b> A bad decrypt almost always means
/// "the protector changed under the file" — e.g. AES-GCM master key rotated without
/// re-encrypting the override file, or DPAPI scope flipped between CurrentUser/
/// LocalMachine. Silently letting <c>enc:v1:...</c> through as the configuration
/// value would make every consumer see ciphertext garbage instead of the secret,
/// almost always producing later-stage 500s with no obvious source. Failing the
/// configuration load gives the operator a clear, actionable startup error pointing
/// at the offending key.</para>
/// </summary>
public sealed class EncryptingJsonConfigurationProvider : JsonConfigurationProvider
{
    public const string EncryptedValuePrefix = "enc:v1:";

    private readonly ISecretProtector _protector;
    private readonly string _sourcePath;

    public EncryptingJsonConfigurationProvider(EncryptingJsonConfigurationSource source) : base(source)
    {
        _protector = source.Protector;
        _sourcePath = source.Path ?? "<unset>";
    }

    public override void Load(Stream stream)
    {
        base.Load(stream);
        DecryptInPlace();
    }

    /// <summary>
    /// Re-encrypt a plaintext value into the persisted form expected by this provider.
    /// Exposed as static so the Save-side writer (controllers / settings probe) can
    /// produce the prefix without taking a dependency on the provider class itself.
    /// </summary>
    public static string EncryptForPersist(string plaintext, ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(protector);
        var blob = protector.Protect(plaintext);
        return EncryptedValuePrefix + Convert.ToBase64String(blob);
    }

    /// <summary>
    /// Recognise a value that's already in encrypted form. Save-side helpers use this
    /// to avoid double-encrypting when an unchanged secret is round-tripped.
    /// </summary>
    public static bool LooksEncrypted(string? value) =>
        value is not null && value.StartsWith(EncryptedValuePrefix, StringComparison.Ordinal);

    private void DecryptInPlace()
    {
        // Snapshot the keys so we can mutate Data without enumeration-mutation issues.
        var keys = new List<string>(Data.Keys);
        foreach (var key in keys)
        {
            var value = Data[key];
            if (!LooksEncrypted(value)) continue;

            var payload = value!.AsSpan(EncryptedValuePrefix.Length);
            byte[] blob;
            try
            {
                blob = Convert.FromBase64String(payload.ToString());
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    BuildFailureMessage(key, "the base64 payload after `enc:v1:` is malformed"),
                    ex);
            }

            string plaintext;
            try
            {
                plaintext = _protector.Unprotect(blob);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    BuildFailureMessage(key,
                        $"the active secret protector ({_protector.ProviderName}) rejected the ciphertext — " +
                        "likely the AES-GCM master key, DPAPI scope, or host identity has changed since this " +
                        "value was written"),
                    ex);
            }

            Data[key] = plaintext;
        }
    }

    private string BuildFailureMessage(string key, string detail) =>
        $"Failed to decrypt the encrypted configuration value for key '{key}' in '{_sourcePath}': {detail}. " +
        "Either restore the original protector configuration, delete the offending entry from the runtime " +
        "overrides file, or re-enter the secret through the Admin Settings UI so it is written under the " +
        "current protector. Refusing to start with a value that would surface as ciphertext to consumers.";
}
