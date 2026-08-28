using Microsoft.Extensions.Configuration.Json;
using NodePilot.Core.Interfaces;

namespace NodePilot.Api.Configuration;

/// <summary>
/// JSON configuration provider that transparently decrypts values written with the
/// <c>enc:v1:&lt;base64&gt;</c> prefix, using the source's <see cref="ISecretProtector"/>.
/// Everything else passes through unchanged via <see cref="JsonConfigurationProvider"/>.
///
/// <para>The Admin Settings API persists user-edited secrets (LDAP password, SMTP password,
/// LLM API key, etc.) into <c>appsettings.runtime.json</c>. Encrypting them in place avoids
/// storing plaintext secrets on disk while keeping the override file a single unit, so an
/// unauthorized reader sees only base64 blobs.</para>
///
/// <para>A decryption failure means the protector no longer matches how the value was
/// encrypted — for example a rotated AES-GCM master key or a changed DPAPI scope. Failing
/// the configuration load gives the operator a clear startup error instead of letting
/// consumers see ciphertext in place of the secret.</para>
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
