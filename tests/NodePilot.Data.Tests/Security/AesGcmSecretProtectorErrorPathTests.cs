using FluentAssertions;
using NodePilot.Data.Security;
using Xunit;

namespace NodePilot.Data.Tests.Security;

/// <summary>
/// Covers the constructor validation and the <see cref="AesGcmSecretProtector.Protect"/>
/// failure/metrics catch-branch that the happy-path tests in
/// <see cref="AesGcmSecretProtectorTests"/> do not reach.
/// </summary>
public class AesGcmSecretProtectorErrorPathTests
{
    private static byte[] DeterministicKey()
    {
        var k = new byte[32];
        for (var i = 0; i < k.Length; i++) k[i] = (byte)(i + 1);
        return k;
    }

    [Fact]
    public void Ctor_WrongLengthKey_ThrowsArgumentException()
    {
        var act = () => new AesGcmSecretProtector(new byte[16]);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*exactly 32 bytes*");
    }

    [Fact]
    public void Ctor_KeyTooLong_ThrowsArgumentException()
    {
        var act = () => new AesGcmSecretProtector(new byte[48]);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*32 bytes*");
    }

    [Fact]
    public void Ctor_NullKey_ThrowsArgumentNullException()
    {
        var act = () => new AesGcmSecretProtector(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Protect_NullPlaintext_ThrowsThroughFailureCatch()
    {
        // Encoding.UTF8.GetBytes(null) throws inside the try; the catch records the failure
        // metric and rethrows — so a null plaintext surfaces as ArgumentNullException.
        var p = new AesGcmSecretProtector(DeterministicKey());
        var act = () => p.Protect(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Protect_EmptyString_RoundTripsToEmpty()
    {
        // Zero-length plaintext is valid: ciphertext length 0, envelope = header+nonce+tag.
        var p = new AesGcmSecretProtector(DeterministicKey());
        var blob = p.Protect(string.Empty);
        p.Unprotect(blob).Should().Be(string.Empty);
    }
}
