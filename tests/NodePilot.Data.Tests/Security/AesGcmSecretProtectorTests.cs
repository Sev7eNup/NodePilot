using System.Security.Cryptography;
using FluentAssertions;
using NodePilot.Data.Security;
using Xunit;

namespace NodePilot.Data.Tests.Security;

/// <summary>
/// Tests for the AES-GCM provider that backs <see cref="AesGcmSecretProtector"/>.
/// The wire format is contractual (rows persist across deployments), so format-shape
/// changes need careful migration planning — these tests pin the contract.
/// </summary>
public class AesGcmSecretProtectorTests
{
    private static byte[] DeterministicKey()
    {
        // All-non-zero test key. The protector rejects all-zero.
        var k = new byte[32];
        for (var i = 0; i < k.Length; i++) k[i] = (byte)(i + 1);
        return k;
    }

    [Fact]
    public void RoundTrip_RecoversOriginalPlaintext()
    {
        var p = new AesGcmSecretProtector(DeterministicKey());
        var blob = p.Protect("hunter2!");
        p.Unprotect(blob).Should().Be("hunter2!");
    }

    [Fact]
    public void Protect_TwoCalls_ProduceDifferentBlobs_DueToRandomNonce()
    {
        var p = new AesGcmSecretProtector(DeterministicKey());
        var a = p.Protect("same plaintext");
        var b = p.Protect("same plaintext");
        a.Should().NotEqual(b, "AES-GCM with random nonce must produce distinct ciphertexts on every call");
    }

    [Fact]
    public void Protect_OutputStartsWithVersionByte()
    {
        var p = new AesGcmSecretProtector(DeterministicKey());
        var blob = p.Protect("x");
        blob[0].Should().Be(0x01, "wire format reserves byte 0 for an envelope version tag");
    }

    [Fact]
    public void Unprotect_BlobShorterThanEnvelope_Throws()
    {
        var p = new AesGcmSecretProtector(DeterministicKey());
        var act = () => p.Unprotect(new byte[10]);
        act.Should().Throw<CryptographicException>()
            .WithMessage("*shorter than the minimum envelope*");
    }

    [Fact]
    public void Unprotect_UnknownVersionByte_Throws()
    {
        var p = new AesGcmSecretProtector(DeterministicKey());
        var blob = p.Protect("v");
        blob[0] = 0xFF;
        var act = () => p.Unprotect(blob);
        act.Should().Throw<CryptographicException>()
            .WithMessage("*Unknown AES-GCM envelope version*");
    }

    [Fact]
    public void Unprotect_TamperedCiphertext_FailsWithCryptoException()
    {
        var p = new AesGcmSecretProtector(DeterministicKey());
        var blob = p.Protect("authenticated payload");
        // Flip one bit in the ciphertext middle. GCM's auth tag must reject this.
        blob[blob.Length - 20] ^= 0x01;
        var act = () => p.Unprotect(blob);
        act.Should().Throw<CryptographicException>("GCM authenticator must catch tampered ciphertext");
    }

    [Fact]
    public void Unprotect_BlobFromDifferentKey_FailsWithCryptoException()
    {
        var keyA = DeterministicKey();
        var keyB = DeterministicKey();
        keyB[0] = 0xFF; // diverge by one byte
        var pa = new AesGcmSecretProtector(keyA);
        var pb = new AesGcmSecretProtector(keyB);

        var blob = pa.Protect("ours");
        var act = () => pb.Unprotect(blob);
        act.Should().Throw<CryptographicException>("decrypt with the wrong key must fail");
    }

    [Fact]
    public void DecodeMasterKey_Empty_ThrowsArgumentException()
    {
        var act = () => AesGcmSecretProtector.DecodeMasterKey("");
        act.Should().Throw<ArgumentException>().WithMessage("*required*");
    }

    [Fact]
    public void DecodeMasterKey_NotBase64_ThrowsArgumentException()
    {
        var act = () => AesGcmSecretProtector.DecodeMasterKey("not!base64!@#");
        act.Should().Throw<ArgumentException>().WithMessage("*not valid base64*");
    }

    [Fact]
    public void DecodeMasterKey_WrongLength_ThrowsArgumentException()
    {
        var shortKey = Convert.ToBase64String(new byte[16]);
        var act = () => AesGcmSecretProtector.DecodeMasterKey(shortKey);
        act.Should().Throw<ArgumentException>().WithMessage("*32 bytes*");
    }

    [Fact]
    public void DecodeMasterKey_AllZeros_ThrowsArgumentException()
    {
        var zeroKey = Convert.ToBase64String(new byte[32]);
        var act = () => AesGcmSecretProtector.DecodeMasterKey(zeroKey);
        act.Should().Throw<ArgumentException>().WithMessage("*all zeros*");
    }

    [Fact]
    public void DecodeMasterKey_ValidKey_ReturnsBytes()
    {
        var b64 = Convert.ToBase64String(DeterministicKey());
        var key = AesGcmSecretProtector.DecodeMasterKey(b64);
        key.Should().Equal(DeterministicKey());
    }

    [Fact]
    public void ProviderName_Is_AesGcm()
    {
        new AesGcmSecretProtector(DeterministicKey()).ProviderName.Should().Be("AesGcm");
    }
}
