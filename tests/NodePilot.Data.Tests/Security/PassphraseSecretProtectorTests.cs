using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using NodePilot.Data.Security;
using Xunit;

namespace NodePilot.Data.Tests.Security;

/// <summary>
/// Crypto-level guarantees of <see cref="PassphraseSecretProtector"/> (the passphrase-based secret
/// encryption used by the system-configuration backup/restore feature, ADR 0001): per-field
/// round-trip, wrong-passphrase rejection, whole-file MAC, verifier check, and the
/// subkey-separation invariant (K14 — encKey ≠ macKey ≠ verifierKey).
/// </summary>
public class PassphraseSecretProtectorTests
{
    private const string Pass = "correct horse battery staple";
    // Low iteration count keeps the test fast; production uses DefaultIterations.
    private const int FastIters = 1000;

    private static PassphraseSecretProtector Derive(string passphrase, byte[] salt) =>
        PassphraseSecretProtector.Derive(passphrase, salt, FastIters);

    [Fact]
    public void Field_RoundTrip_RecoversPlaintext()
    {
        var salt = PassphraseSecretProtector.GenerateSalt();
        var p = Derive(Pass, salt);

        var blob = p.Protect("s3cr3t-value");
        p.Unprotect(blob).Should().Be("s3cr3t-value");
    }

    [Fact]
    public void SamePassphrase_DifferentSalt_ProducesDifferentKeys()
    {
        var a = Derive(Pass, PassphraseSecretProtector.GenerateSalt());
        var b = Derive(Pass, PassphraseSecretProtector.GenerateSalt());

        var blob = a.Protect("x");
        var act = () => b.Unprotect(blob);
        act.Should().Throw<CryptographicException>(
            "a different per-file salt must yield independent keys even for the same passphrase");
    }

    [Fact]
    public void WrongPassphrase_FailsToDecryptField()
    {
        var salt = PassphraseSecretProtector.GenerateSalt();
        var good = Derive(Pass, salt);
        var blob = good.Protect("x");

        var bad = Derive("wrong passphrase", salt);
        var act = () => bad.Unprotect(blob);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Verifier_AcceptsCorrectPassphrase_RejectsWrongOne()
    {
        var salt = PassphraseSecretProtector.GenerateSalt();
        var verifier = Derive(Pass, salt).CreateVerifier();

        Derive(Pass, salt).VerifyPassphrase(verifier).Should().BeTrue();
        Derive("nope", salt).VerifyPassphrase(verifier).Should().BeFalse();
    }

    [Fact]
    public void VerifyPassphrase_OnTamperedVerifier_ReturnsFalse_DoesNotThrow()
    {
        var salt = PassphraseSecretProtector.GenerateSalt();
        var p = Derive(Pass, salt);
        var verifier = p.CreateVerifier();
        verifier[^1] ^= 0xFF; // flip a tag byte

        p.VerifyPassphrase(verifier).Should().BeFalse();
    }

    [Fact]
    public void Mac_RoundTrips_AndDetectsTampering()
    {
        var salt = PassphraseSecretProtector.GenerateSalt();
        var p = Derive(Pass, salt);
        var payload = Encoding.UTF8.GetBytes("""{"schema":"nodepilot-system-backup/v1"}""");

        var mac = p.ComputeMac(payload);
        p.VerifyMac(payload, mac).Should().BeTrue();

        var tampered = (byte[])payload.Clone();
        tampered[1] ^= 0xFF;
        p.VerifyMac(tampered, mac).Should().BeFalse(
            "a single flipped byte in the plaintext payload must fail the whole-file MAC");
    }

    [Fact]
    public void Mac_WrongPassphrase_DoesNotVerify()
    {
        var salt = PassphraseSecretProtector.GenerateSalt();
        var payload = Encoding.UTF8.GetBytes("payload");
        var mac = Derive(Pass, salt).ComputeMac(payload);

        Derive("other", salt).VerifyMac(payload, mac).Should().BeFalse();
    }

    [Fact]
    public void Subkeys_AreIndependent_EncBlobIsNotAValidVerifier()
    {
        // K14: if encKey == verifierKey, a field blob would validate as a verifier. It must not.
        var salt = PassphraseSecretProtector.GenerateSalt();
        var p = Derive(Pass, salt);

        var encBlobOfToken = p.Protect("nodepilot-system-backup/v1/verifier-ok");
        p.VerifyPassphrase(encBlobOfToken).Should().BeFalse(
            "encKey and verifierKey must be independent — an enc-blob must not pass the verifier check");
    }

    [Fact]
    public void EmptyPassphrase_Throws()
    {
        var salt = PassphraseSecretProtector.GenerateSalt();
        var act = () => PassphraseSecretProtector.Derive("", salt, FastIters);
        act.Should().Throw<ArgumentException>();
    }
}
