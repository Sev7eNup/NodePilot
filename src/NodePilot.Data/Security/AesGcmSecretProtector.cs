using System.Security.Cryptography;
using System.Text;
using NodePilot.Core.Interfaces;

namespace NodePilot.Data.Security;

/// <summary>
/// Cross-host-portable <see cref="ISecretProtector"/> backed by AES-256-GCM with a
/// shared Master-Key from <c>Secrets:MasterKey</c> (base64-encoded ≥32 bytes).
/// <para>
/// Use this provider in active/passive HA: DPAPI's machine binding makes it impossible
/// for Node B to decrypt a credential that was written on Node A. AES-GCM with a key in
/// config solves that — both nodes have the same key in their <c>appsettings.Production.json</c>.
/// The trade-off is that the master key now lives in plaintext on disk; protect the
/// settings file with file-system ACLs (`icacls` / `chmod 600`).
/// </para>
/// <para>
/// Wire format (binary, persisted as-is in the existing <c>byte[]</c> column):
/// <c>[1 byte version=0x01] [12 bytes nonce] [N bytes ciphertext] [16 bytes auth tag]</c>.
/// The version prefix is reserved for future key-rotation envelopes; today it's always
/// 0x01. <see cref="Unprotect"/> rejects any blob whose first byte is anything else.
/// </para>
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private readonly byte[] _key;

    public string ProviderName => "AesGcm";

    public AesGcmSecretProtector(byte[] masterKey)
    {
        ArgumentNullException.ThrowIfNull(masterKey);
        if (masterKey.Length != 32)
            throw new ArgumentException(
                $"AES-GCM master key must be exactly 32 bytes (256 bit). Got {masterKey.Length}.",
                nameof(masterKey));
        _key = masterKey;
    }

    public byte[] Protect(string plaintext) => DataMetrics.MeasureCrypto("encrypt", ProviderName, () =>
        SecretEnvelope.Seal(Encoding.UTF8.GetBytes(plaintext), _key));

    private const string TooShortMessage = "AES-GCM blob is shorter than the minimum envelope (header + nonce + tag).";

    private static string UnknownVersionMessage(byte actual) =>
        $"Unknown AES-GCM envelope version 0x{actual:X2}. Expected {SecretEnvelope.ExpectedVersionHex}. " +
        "Was the row written by a different ISecretProtector?";

    public string Unprotect(byte[] blob)
    {
        // Header validation runs OUTSIDE the measured region, as it always has: a malformed blob
        // is a caller error, not a crypto failure, and must not land in the failed-decrypt series.
        SecretEnvelope.ValidateHeader(blob, TooShortMessage, UnknownVersionMessage);

        return DataMetrics.MeasureCrypto("decrypt", ProviderName, () =>
            Encoding.UTF8.GetString(SecretEnvelope.Open(blob, _key, TooShortMessage, UnknownVersionMessage)));
    }

    /// <summary>
    /// Decode a base64-encoded master key string. Validates length and rejects suspicious
    /// values (placeholder-y all-zeros, dev-default-y "AAAA..."). Throws on any problem so
    /// the operator gets a clear startup error.
    /// </summary>
    public static byte[] DecodeMasterKey(string base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
            throw new ArgumentException("Secrets:MasterKey is required when Secrets:Provider=AesGcm.", nameof(base64Key));
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(base64Key);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("Secrets:MasterKey is not valid base64.", nameof(base64Key), ex);
        }
        if (decoded.Length != 32)
            throw new ArgumentException(
                $"Secrets:MasterKey must decode to exactly 32 bytes. Got {decoded.Length} byte(s) — generate with " +
                "PowerShell `$r=[Security.Cryptography.RandomNumberGenerator]::Create();$b=New-Object byte[] 32;" +
                "try{$r.GetBytes($b);[Convert]::ToBase64String($b)}finally{$r.Dispose();[Array]::Clear($b,0,$b.Length)}`.",
                nameof(base64Key));
        if (decoded.All(b => b == 0))
            throw new ArgumentException("Secrets:MasterKey is all zeros — refusing to use a degenerate key.", nameof(base64Key));
        return decoded;
    }
}
