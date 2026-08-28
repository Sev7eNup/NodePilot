namespace NodePilot.Core.Interfaces;

/// <summary>
/// Pluggable encryption layer for secrets at rest. <c>CredentialStore</c> (machine passwords)
/// and <c>GlobalVariableStore</c> (secret-flagged global variables) route their encrypt and
/// decrypt calls through this interface, so changing the provider is a single DI registration.
/// <para>
/// The byte arrays an implementation produces are persisted as-is and read back by the same
/// provider later, so the wire format has to stay stable. <see cref="ProviderName"/> records
/// which provider produced a blob and is emitted in audit details, so a later migration can
/// tell the formats apart without a schema change.
/// </para>
/// </summary>
public interface ISecretProtector
{
    /// <summary>
    /// Stable identifier exposed in audit events, for example "Dpapi" or "AesGcm". Enables
    /// filter migration sweeps and to show operators which provider owns each row.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Encrypts plaintext into an opaque blob for DB persistence. The format is
    /// provider-internal: moving to another provider means re-encrypting the whole store,
    /// not interpreting a foreign provider's output.
    /// </summary>
    byte[] Protect(string plaintext);

    /// <summary>
    /// Reverses <see cref="Protect"/>. Throws when the blob came from a different provider,
    /// was tampered with, or the configured key cannot decrypt it. The caller
    /// (<c>CredentialStore</c> or <c>GlobalVariableStore</c>) surfaces a clean error to the
    /// workflow engine rather than the raw cryptographic exception.
    /// </summary>
    string Unprotect(byte[] blob);
}
