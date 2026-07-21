using System.Security.Cryptography;
using System.Text;
using NodePilot.Core.Interfaces;

namespace NodePilot.Data.Security;

/// <summary>
/// Passphrase-derived <see cref="ISecretProtector"/> used exclusively by the system-backup
/// feature (ADR 0001 — the disaster-recovery `.npbackup` snapshot format). Unlike the at-rest
/// protectors (DPAPI / AES-GCM master key), this one derives its keys from an operator-supplied
/// passphrase + a random per-file salt, so a <c>.npbackup</c> archive is portable to any host:
/// whoever has the passphrase can restore it.
///
/// <para>
/// Key separation (a design requirement from ADR 0001, tracked there as item K14): the
/// passphrase is run through PBKDF2-SHA256 into a single 256-bit master secret, which is then
/// split via HKDF-Expand into three independent subkeys —
/// <c>encKey</c> (AES-256-GCM of the per-field <c>$enc</c> blobs), <c>macKey</c> (whole-file
/// HMAC-SHA256 over the canonical JSON) and <c>verifierKey</c> (encrypts a known token so a
/// wrong passphrase is detected before any write). The same PBKDF2 output is never used for
/// more than one purpose.
/// </para>
///
/// <para>
/// The <see cref="ISecretProtector"/> surface (<see cref="Protect"/> / <see cref="Unprotect"/>)
/// covers only the per-field encryption with <c>encKey</c>; the MAC and verifier are exposed as
/// concrete members because they have no meaning for the generic at-rest abstraction.
/// Wire format of a field blob matches <see cref="AesGcmSecretProtector"/>:
/// <c>[1 byte version=0x01] [12 byte nonce] [N byte ciphertext] [16 byte tag]</c>.
/// </para>
/// </summary>
public sealed class PassphraseSecretProtector : ISecretProtector
{
    public const string KdfName = "PBKDF2-SHA256";
    public const int DefaultIterations = 600_000;
    public const int SaltSize = 16;

    private const byte Version = 0x01;
    private const int NonceSize = 12;   // GCM standard
    private const int TagSize = 16;     // 128-bit GCM tag
    private const int KeySize = 32;     // 256-bit subkeys

    // Distinct HKDF info labels keep the three derived subkeys cryptographically independent.
    private static readonly byte[] EncInfo = "nodepilot-backup/v1/enc"u8.ToArray();
    private static readonly byte[] MacInfo = "nodepilot-backup/v1/mac"u8.ToArray();
    private static readonly byte[] VerifierInfo = "nodepilot-backup/v1/verifier"u8.ToArray();

    // A fixed token encrypted under verifierKey. Restore decrypts it before touching the DB;
    // failure means "wrong passphrase" rather than a half-applied restore.
    private static readonly byte[] VerifierToken = "nodepilot-system-backup/v1/verifier-ok"u8.ToArray();

    private readonly byte[] _encKey;
    private readonly byte[] _macKey;
    private readonly byte[] _verifierKey;

    public string ProviderName => "PassphraseAesGcm";

    private PassphraseSecretProtector(byte[] encKey, byte[] macKey, byte[] verifierKey)
    {
        _encKey = encKey;
        _macKey = macKey;
        _verifierKey = verifierKey;
    }

    /// <summary>Generates a fresh random salt for a new backup file.</summary>
    public static byte[] GenerateSalt() => RandomNumberGenerator.GetBytes(SaltSize);

    /// <summary>
    /// Derives the three subkeys from <paramref name="passphrase"/> + <paramref name="salt"/>.
    /// Both export (fresh salt) and restore (salt read from the file header) call this.
    /// </summary>
    public static PassphraseSecretProtector Derive(string passphrase, byte[] salt, int iterations = DefaultIterations)
    {
        if (string.IsNullOrEmpty(passphrase))
            throw new ArgumentException("Passphrase must not be empty.", nameof(passphrase));
        ArgumentNullException.ThrowIfNull(salt);
        if (salt.Length < SaltSize)
            throw new ArgumentException($"Salt must be at least {SaltSize} bytes.", nameof(salt));
        if (iterations < 1)
            throw new ArgumentOutOfRangeException(nameof(iterations));

        var master = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, KeySize);
        try
        {
            // master is already uniformly random → HKDF-Expand (no extract salt needed) is sufficient.
            var enc = HKDF.Expand(HashAlgorithmName.SHA256, master, KeySize, EncInfo);
            var mac = HKDF.Expand(HashAlgorithmName.SHA256, master, KeySize, MacInfo);
            var ver = HKDF.Expand(HashAlgorithmName.SHA256, master, KeySize, VerifierInfo);
            return new PassphraseSecretProtector(enc, mac, ver);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    public byte[] Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return EncryptGcm(_encKey, Encoding.UTF8.GetBytes(plaintext));
    }

    public string Unprotect(byte[] blob)
    {
        var plain = DecryptGcm(_encKey, blob);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>HMAC-SHA256 over the canonical file bytes using the dedicated <c>macKey</c> (the MAC-subkey requirement from ADR 0001, item K5).</summary>
    public byte[] ComputeMac(byte[] canonicalBytes)
    {
        ArgumentNullException.ThrowIfNull(canonicalBytes);
        return HMACSHA256.HashData(_macKey, canonicalBytes);
    }

    /// <summary>Constant-time comparison of a recomputed MAC against the stored one.</summary>
    public bool VerifyMac(byte[] canonicalBytes, byte[] expectedMac)
    {
        ArgumentNullException.ThrowIfNull(expectedMac);
        var actual = ComputeMac(canonicalBytes);
        return CryptographicOperations.FixedTimeEquals(actual, expectedMac);
    }

    /// <summary>Produces the verifier blob stored in the file header (<c>crypto.verifier</c>).</summary>
    public byte[] CreateVerifier() => EncryptGcm(_verifierKey, VerifierToken);

    /// <summary>
    /// Validates that this protector (i.e. this passphrase + salt) can decrypt the stored
    /// verifier. Returns false for a wrong passphrase or a tampered verifier — never throws.
    /// </summary>
    public bool VerifyPassphrase(byte[] verifierBlob)
    {
        ArgumentNullException.ThrowIfNull(verifierBlob);
        try
        {
            var decrypted = DecryptGcm(_verifierKey, verifierBlob);
            return CryptographicOperations.FixedTimeEquals(decrypted, VerifierToken);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static byte[] EncryptGcm(byte[] key, byte[] plain)
    {
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plain.Length];
        var tag = new byte[TagSize];
        using (var gcm = new AesGcm(key, TagSize))
            gcm.Encrypt(nonce, plain, ciphertext, tag);

        var blob = new byte[1 + NonceSize + ciphertext.Length + TagSize];
        blob[0] = Version;
        Buffer.BlockCopy(nonce, 0, blob, 1, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, blob, 1 + NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, blob, 1 + NonceSize + ciphertext.Length, TagSize);
        return blob;
    }

    private static byte[] DecryptGcm(byte[] key, byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length < 1 + NonceSize + TagSize)
            throw new CryptographicException("Backup secret blob is shorter than the minimum envelope.");
        if (blob[0] != Version)
            throw new CryptographicException($"Unknown backup secret envelope version 0x{blob[0]:X2}.");

        var ciphertextLength = blob.Length - 1 - NonceSize - TagSize;
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
}
