using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using NodePilot.Core.Clients;
using Xunit;

namespace NodePilot.Cli.Tests.Tls;

public sealed class CertificatePinTests
{
    private const string Canonical = "A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F90";

    [Fact]
    public void Parse_UppercaseHexWithoutSeparators_ReturnsCanonicalValue()
    {
        CertificatePin.Parse(Canonical, out var normalized).Should().Be(CertificatePinFormat.Valid);
        normalized.Should().Be(Canonical);
    }

    [Theory]
    [InlineData("a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90")]
    [InlineData("A1:B2:C3:D4:E5:F6:07:18:29:3A:4B:5C:6D:7E:8F:90:A1:B2:C3:D4:E5:F6:07:18:29:3A:4B:5C:6D:7E:8F:90")]
    [InlineData("a1 b2 c3 d4 e5 f6 07 18 29 3a 4b 5c 6d 7e 8f 90 a1 b2 c3 d4 e5 f6 07 18 29 3a 4b 5c 6d 7e 8f 90")]
    [InlineData("sha256:A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F90")]
    public void Parse_AcceptedSpellings_NormalizeToTheSameValue(string raw)
    {
        CertificatePin.Parse(raw, out var normalized).Should().Be(CertificatePinFormat.Valid);
        normalized.Should().Be(Canonical);
    }

    [Fact]
    public void Parse_Sha1Thumbprint_IsReportedSeparately()
    {
        // What Windows shows as "Thumbprint" — the mistake worth naming explicitly.
        CertificatePin.Parse(new string('A', 40), out _).Should().Be(CertificatePinFormat.Sha1Thumbprint);
    }

    [Theory]
    [InlineData("not-hex-at-all")]
    [InlineData("A1B2C3")]
    [InlineData("A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F9012")]
    public void Parse_UnusableValue_IsMalformed(string raw)
        => CertificatePin.Parse(raw, out _).Should().Be(CertificatePinFormat.Malformed);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NoValue_IsEmpty(string? raw)
        => CertificatePin.Parse(raw, out _).Should().Be(CertificatePinFormat.Empty);

    [Fact]
    public void Compute_Certificate_MatchesFrameworkFingerprintAndPinFormat()
    {
        using var certificate = TestCertificates.CreateSelfSigned("pin.test");

        var fingerprint = CertificatePin.Compute(certificate);

        fingerprint.Should().Be(certificate.GetCertHashString(HashAlgorithmName.SHA256));
        // Same shape the Electron shell pins against: 64 uppercase hex characters, no separators.
        CertificatePin.Parse(fingerprint, out var normalized).Should().Be(CertificatePinFormat.Valid);
        normalized.Should().Be(fingerprint);
    }

    [Fact]
    public void Matches_DiffersOnlyInCase_IsAMatch()
        => CertificatePin.Matches(Canonical, Canonical.ToLowerInvariant()).Should().BeTrue();

    [Fact]
    public void Matches_MissingValue_IsNeverAMatch()
    {
        CertificatePin.Matches(null, Canonical).Should().BeFalse();
        CertificatePin.Matches(Canonical, "").Should().BeFalse();
    }
}
