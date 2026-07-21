using System.Diagnostics;
using System.Diagnostics.Metrics;
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
    private const byte Version = 0x01;
    private const int NonceSize = 12;     // GCM standard
    private const int TagSize = 16;       // 128-bit GCM tag

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

    public byte[] Protect(string plaintext)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var plain = Encoding.UTF8.GetBytes(plaintext);
            var nonce = new byte[NonceSize];
            RandomNumberGenerator.Fill(nonce);
            var ciphertext = new byte[plain.Length];
            var tag = new byte[TagSize];

            // .NET 10's AesGcm constructor takes the key + tag size — the latter is required
            // since 9.0 (was inferable before; explicit prevents later breaking-change pain).
            using var gcm = new AesGcm(_key, TagSize);
            gcm.Encrypt(nonce, plain, ciphertext, tag);

            var blob = new byte[1 + NonceSize + ciphertext.Length + TagSize];
            blob[0] = Version;
            Buffer.BlockCopy(nonce, 0, blob, 1, NonceSize);
            Buffer.BlockCopy(ciphertext, 0, blob, 1 + NonceSize, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, blob, 1 + NonceSize + ciphertext.Length, TagSize);

            sw.Stop();
            DataMetrics.CredentialCryptoCalls.Add(1,
                new("operation", "encrypt"),
                new("provider", ProviderName),
                new("result", "success"));
            var tags = new TagList { new("operation", "encrypt"), new("provider", ProviderName) };
            DataMetrics.CredentialCryptoDuration.Record(sw.Elapsed.TotalMilliseconds, tags);
            return blob;
        }
        catch
        {
            sw.Stop();
            DataMetrics.CredentialCryptoCalls.Add(1,
                new("operation", "encrypt"),
                new("provider", ProviderName),
                new("result", "failure"));
            var tags = new TagList { new("operation", "encrypt"), new("provider", ProviderName) };
            DataMetrics.CredentialCryptoDuration.Record(sw.Elapsed.TotalMilliseconds, tags);
            throw;
        }
    }

    public string Unprotect(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length < 1 + NonceSize + TagSize)
            throw new CryptographicException("AES-GCM blob is shorter than the minimum envelope (header + nonce + tag).");
        if (blob[0] != Version)
            throw new CryptographicException(
                $"Unknown AES-GCM envelope version 0x{blob[0]:X2}. Expected 0x{Version:X2}. " +
                "Was the row written by a different ISecretProtector?");

        var sw = Stopwatch.StartNew();
        try
        {
            var ciphertextLength = blob.Length - 1 - NonceSize - TagSize;
            var nonce = new byte[NonceSize];
            var ciphertext = new byte[ciphertextLength];
            var tag = new byte[TagSize];
            Buffer.BlockCopy(blob, 1, nonce, 0, NonceSize);
            Buffer.BlockCopy(blob, 1 + NonceSize, ciphertext, 0, ciphertextLength);
            Buffer.BlockCopy(blob, 1 + NonceSize + ciphertextLength, tag, 0, TagSize);

            var plain = new byte[ciphertextLength];
            using var gcm = new AesGcm(_key, TagSize);
            gcm.Decrypt(nonce, ciphertext, tag, plain);

            sw.Stop();
            DataMetrics.CredentialCryptoCalls.Add(1,
                new("operation", "decrypt"),
                new("provider", ProviderName),
                new("result", "success"));
            var tags = new TagList { new("operation", "decrypt"), new("provider", ProviderName) };
            DataMetrics.CredentialCryptoDuration.Record(sw.Elapsed.TotalMilliseconds, tags);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            sw.Stop();
            DataMetrics.CredentialCryptoCalls.Add(1,
                new("operation", "decrypt"),
                new("provider", ProviderName),
                new("result", "failure"));
            var tags = new TagList { new("operation", "decrypt"), new("provider", ProviderName) };
            DataMetrics.CredentialCryptoDuration.Record(sw.Elapsed.TotalMilliseconds, tags);
            throw;
        }
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
