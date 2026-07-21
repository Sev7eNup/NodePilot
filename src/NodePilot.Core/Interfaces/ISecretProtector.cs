namespace NodePilot.Core.Interfaces;

/// <summary>
/// Pluggable encryption layer for secrets at rest. Both <c>CredentialStore</c> (machine
/// passwords) and <c>GlobalVariableStore</c> (secret-flagged global variables) route their
/// encrypt/decrypt calls through this interface, so swapping DPAPI for AES-GCM or HashiCorp
/// Vault is a single registration change in DI.
/// <para>
/// Implementations must be deterministic about the wire format: the byte arrays they
/// produce will be persisted to the DB as-is and read back by the same provider months
/// later. <see cref="ProviderName"/> is the operator-visible identifier of which provider
/// produced the blob — emitted in audit details so a future migration can tell DPAPI-era
/// rows from AES-GCM-era rows without a schema change.
/// </para>
/// </summary>
public interface ISecretProtector
{
    /// <summary>
    /// Stable identifier exposed in audit events ("Dpapi", "AesGcm", later
    /// "HashiCorpVault"). Used to filter migration sweeps and to surface in operator
    /// dashboards which provider currently owns each row.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Encrypts plaintext into an opaque byte blob suitable for DB persistence. The blob
    /// format is provider-internal — a future migration to a different provider must
    /// re-encrypt the entire store, NOT try to interpret a foreign provider's output.
    /// </summary>
    byte[] Protect(string plaintext);

    /// <summary>
    /// Reverses <see cref="Protect"/>. Throws if the blob was produced by a different
    /// provider, was tampered with, or the configured key cannot decrypt it. The caller
    /// (<c>CredentialStore</c> / <c>GlobalVariableStore</c>) is expected to surface a
    /// clean error to the workflow engine, not the raw cryptographic exception.
    /// </summary>
    string Unprotect(byte[] blob);
}
