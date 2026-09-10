using System.Net.Security;
using System.Security.Authentication;
using FluentAssertions;
using NodePilot.Core.Clients;
using Xunit;

namespace NodePilot.Cli.Tests.Tls;

/// <summary>
/// The classifier names one primary cause but must not lose the others. .NET raises the name and
/// chain policy errors independently, and the expiry check sits ahead of both.
/// </summary>
public sealed class NetworkFailureAnalyzerTests
{
    private const SslPolicyErrors ChainErrors = SslPolicyErrors.RemoteCertificateChainErrors;
    private const SslPolicyErrors NameMismatch = SslPolicyErrors.RemoteCertificateNameMismatch;

    [Fact]
    public void Analyze_ChainErrorsAndNameMismatch_ReportsUntrustedChainAndFlagsTheNameMismatch()
    {
        var info = Analyze(Observation(ChainErrors | NameMismatch));

        info.Tls.Should().Be(TlsFailureKind.UntrustedChain);
        info.HasNameMismatch.Should().BeTrue();
    }

    [Fact]
    public void Analyze_NameMismatchOnly_ReportsNameMismatchAndFlagsIt()
    {
        var info = Analyze(Observation(NameMismatch));

        info.Tls.Should().Be(TlsFailureKind.NameMismatch);
        info.HasNameMismatch.Should().BeTrue();
    }

    [Fact]
    public void Analyze_ChainErrorsOnly_DoesNotFlagANameMismatch()
    {
        var info = Analyze(Observation(ChainErrors));

        info.Tls.Should().Be(TlsFailureKind.UntrustedChain);
        info.HasNameMismatch.Should().BeFalse();
    }

    [Fact]
    public void Analyze_ExpiredAndNameMismatch_ReportsExpiredAndFlagsTheNameMismatch()
    {
        var expired = Observation(ChainErrors | NameMismatch) with
        {
            NotBefore = DateTimeOffset.UtcNow.AddYears(-2),
            NotAfter = DateTimeOffset.UtcNow.AddDays(-1),
        };

        var info = Analyze(expired);

        info.Tls.Should().Be(TlsFailureKind.Expired);
        info.HasNameMismatch.Should().BeTrue();
    }

    private static NetworkFailureInfo Analyze(PresentedCertificateInfo observation)
    {
        var failure = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate was rejected."));
        failure.Data[TlsObservationHandler.ExceptionDataKey] = observation;
        return NetworkFailureAnalyzer.Analyze(failure);
    }

    private static PresentedCertificateInfo Observation(SslPolicyErrors errors) => new()
    {
        RequestHost = "localhost",
        RequestPort = 8443,
        Subject = "CN=np.lab.local",
        Issuer = "CN=np.lab.local",
        Sha256 = "A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F90",
        DnsNames = new[] { "np.lab.local" },
        NotBefore = DateTimeOffset.UtcNow.AddDays(-1),
        NotAfter = DateTimeOffset.UtcNow.AddYears(1),
        PolicyErrors = errors,
    };
}
