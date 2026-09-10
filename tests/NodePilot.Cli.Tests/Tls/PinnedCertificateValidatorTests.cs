using System.Net.Security;
using FluentAssertions;
using NodePilot.Core.Clients;
using Xunit;

namespace NodePilot.Cli.Tests.Tls;

public sealed class PinnedCertificateValidatorTests
{
    private const SslPolicyErrors ChainErrors = SslPolicyErrors.RemoteCertificateChainErrors;
    private const SslPolicyErrors NameMismatch = SslPolicyErrors.RemoteCertificateNameMismatch;

    [Fact]
    public void Evaluate_ValidChainWithoutPin_Accepts()
    {
        using var certificate = TestCertificates.CreateSelfSigned("np.test");

        PinnedCertificateHandlerFactory.Evaluate(
            ClientTlsOptions.None, "np.test", 8443, certificate, null, SslPolicyErrors.None, out var observation)
            .Should().BeTrue();

        observation.PinConfigured.Should().BeFalse();
        observation.AcceptedWithoutVerification.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_ValidChainWithNonMatchingPin_StillAccepts()
    {
        // The pin is additive: a certificate that validates normally must keep working, otherwise
        // a routine renewal would lock out a correctly configured profile.
        using var certificate = TestCertificates.CreateSelfSigned("np.test");
        var options = new ClientTlsOptions(new string('A', 64));

        PinnedCertificateHandlerFactory.Evaluate(
            options, "np.test", 8443, certificate, null, SslPolicyErrors.None, out var observation)
            .Should().BeTrue();

        observation.PinMatched.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_UntrustedChainWithMatchingPin_Accepts()
    {
        using var certificate = TestCertificates.CreateSelfSigned("np.test");
        var options = new ClientTlsOptions(CertificatePin.Compute(certificate));

        PinnedCertificateHandlerFactory.Evaluate(
            options, "np.test", 8443, certificate, null, ChainErrors, out var observation)
            .Should().BeTrue();

        observation.PinMatched.Should().BeTrue();
        observation.AcceptedDespiteNameMismatch.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_UntrustedChainWithMismatchedPin_Rejects()
    {
        using var certificate = TestCertificates.CreateSelfSigned("np.test");
        var options = new ClientTlsOptions(new string('B', 64));

        PinnedCertificateHandlerFactory.Evaluate(
            options, "np.test", 8443, certificate, null, ChainErrors, out var observation)
            .Should().BeFalse();

        observation.PinConfigured.Should().BeTrue();
        observation.PinMatched.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_MismatchedPinWithSkipVerification_StillRejects()
    {
        // A configured pin that does not match is an alarm, not a formality: the bypass must not
        // turn it into a connection.
        using var certificate = TestCertificates.CreateSelfSigned("np.test");
        var options = new ClientTlsOptions(new string('C', 64), SkipVerification: true);

        PinnedCertificateHandlerFactory.Evaluate(
            options, "np.test", 8443, certificate, null, ChainErrors, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void Evaluate_UntrustedChainWithoutPin_Rejects()
    {
        using var certificate = TestCertificates.CreateSelfSigned("np.test");

        PinnedCertificateHandlerFactory.Evaluate(
            ClientTlsOptions.None, "np.test", 8443, certificate, null, ChainErrors, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void Evaluate_UntrustedChainWithSkipVerification_AcceptsAndFlagsIt()
    {
        using var certificate = TestCertificates.CreateSelfSigned("np.test");

        PinnedCertificateHandlerFactory.Evaluate(
            new ClientTlsOptions(SkipVerification: true), "np.test", 8443, certificate, null, ChainErrors,
            out var observation)
            .Should().BeTrue();

        observation.AcceptedWithoutVerification.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_NameMismatchWithMatchingPin_AcceptsAndFlagsOverride()
    {
        using var certificate = TestCertificates.CreateSelfSigned("np.test", "other.test");
        var options = new ClientTlsOptions(CertificatePin.Compute(certificate));

        PinnedCertificateHandlerFactory.Evaluate(
            options, "np.test", 8443, certificate, null, NameMismatch, out var observation)
            .Should().BeTrue();

        observation.AcceptedDespiteNameMismatch.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_NoCertificatePresented_Rejects()
    {
        var options = new ClientTlsOptions(new string('D', 64));

        PinnedCertificateHandlerFactory.Evaluate(
            options, "np.test", 8443, null, null, SslPolicyErrors.RemoteCertificateNotAvailable,
            out var observation)
            .Should().BeFalse();

        observation.HasCertificate.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_AnyOutcome_RecordsThePresentedCertificate()
    {
        using var certificate = TestCertificates.CreateSelfSigned("np.test", "np.test", "localhost");

        PinnedCertificateHandlerFactory.Evaluate(
            ClientTlsOptions.None, "np.test", 8443, certificate, null, ChainErrors, out var observation);

        observation.RequestHost.Should().Be("np.test");
        observation.RequestPort.Should().Be(8443);
        observation.Subject.Should().Be(certificate.Subject);
        observation.Sha256.Should().Be(CertificatePin.Compute(certificate));
        observation.DnsNames.Should().BeEquivalentTo("np.test", "localhost");
        observation.IsSelfSigned.Should().BeTrue();
        observation.PolicyErrors.Should().Be(ChainErrors);
    }

    [Fact]
    public void Evaluate_CombinedChainAndNameErrors_RecordsBothFlagsUnmasked()
    {
        // The clients decide what to report from these flags, so the observation must not collapse
        // them into one.
        using var certificate = TestCertificates.CreateSelfSigned("np.test");

        PinnedCertificateHandlerFactory.Evaluate(
            ClientTlsOptions.None, "localhost", 8443, certificate, null, ChainErrors | NameMismatch,
            out var observation);

        observation.PolicyErrors.Should().HaveFlag(ChainErrors);
        observation.PolicyErrors.Should().HaveFlag(NameMismatch);
    }

    [Fact]
    public void Evaluate_ExpiredCertificate_IsReportedAsExpired()
    {
        using var certificate = TestCertificates.CreateSelfSigned(
            "np.test", DateTimeOffset.UtcNow.AddYears(-2), DateTimeOffset.UtcNow.AddDays(-1));

        PinnedCertificateHandlerFactory.Evaluate(
            ClientTlsOptions.None, "np.test", 8443, certificate, null, ChainErrors, out var observation);

        observation.IsExpired.Should().BeTrue();
    }
}
