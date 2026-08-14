using System.Security.Cryptography;

namespace NodePilot.Data.Security;

/// <summary>
/// The on-disk/in-column AES-GCM envelope every NodePilot secret is wrapped in:
/// <c>[version:1][nonce:12][ciphertext:n][tag:16]</c>.
/// <para>
/// This is a persisted wire format — a stored credential written today must still open years
/// later. It therefore lives in exactly one place: <see cref="AesGcmSecretProtector"/> (credential
/// column) and <see cref="PassphraseSecretProtector"/> (backup rewrap) previously carried
/// byte-compatible copies of the layout, where any edit to one would have silently produced blobs
/// the other could not read.
/// </para>
/// </summary>
internal static class SecretEnvelope
{
    private const byte Version = 0x01;
    private const int NonceSize = 12;   // GCM standard
    private const int TagSize = 16;     // 128-bit GCM tag

    /// <summary>Smallest possible envelope: header + nonce + tag, with empty ciphertext.</summary>
    public const int MinLength = 1 + NonceSize + TagSize;

    public static byte[] Seal(byte[] plain, byte[] key)
    {
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plain.Length];
        var tag = new byte[TagSize];

        // .NET 10's AesGcm constructor takes the key + tag size — the latter is required
        // since 9.0 (was inferable before; explicit prevents later breaking-change pain).
        using (var gcm = new AesGcm(key, TagSize))
            gcm.Encrypt(nonce, plain, ciphertext, tag);

        var blob = new byte[MinLength + ciphertext.Length];
        blob[0] = Version;
        Buffer.BlockCopy(nonce, 0, blob, 1, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, blob, 1 + NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, blob, 1 + NonceSize + ciphertext.Length, TagSize);
        return blob;
    }

    /// <summary>
    /// Rejects a null, truncated or foreign-versioned blob. Separate from <see cref="Open"/> so
    /// a caller can run it outside its metrics scope — a malformed blob is a caller error, not a
    /// crypto failure, and must not land in the failed-decrypt series.
    /// <paramref name="tooShortMessage"/> and <paramref name="unknownVersionMessage"/> keep each
    /// caller's diagnostic wording: the credential path points at a mismatched ISecretProtector,
    /// the backup path at a foreign archive, and an operator needs to know which they are holding.
    /// </summary>
    public static void ValidateHeader(byte[] blob, string tooShortMessage, Func<byte, string> unknownVersionMessage)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length < MinLength)
            throw new CryptographicException(tooShortMessage);
        if (blob[0] != Version)
            throw new CryptographicException(unknownVersionMessage(blob[0]));
    }

    /// <summary>Validates the header (see <see cref="ValidateHeader"/>) and decrypts.</summary>
    public static byte[] Open(byte[] blob, byte[] key, string tooShortMessage, Func<byte, string> unknownVersionMessage)
    {
        ValidateHeader(blob, tooShortMessage, unknownVersionMessage);

        var ciphertextLength = blob.Length - MinLength;
        var nonce = new byte[NonceSize];
        var ciphertext = new byte[ciphertextLength];
        var tag = new byte[TagSize];
        Buffer.BlockCopy(blob, 1, nonce, 0, NonceSize);
        Buffer.BlockCopy(blob, 1 + NonceSize, ciphertext, 0, ciphertextLength);
        Buffer.BlockCopy(blob, 1 + NonceSize + ciphertextLength, tag, 0, TagSize);

        var plain = new byte[ciphertextLength];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Decrypt(nonce, ciphertext, tag, plain);
        return plain;
    }

    /// <summary>Renders the expected version byte for a caller's diagnostic message.</summary>
    public static string ExpectedVersionHex => $"0x{Version:X2}";
}
