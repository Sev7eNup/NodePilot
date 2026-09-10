using System.Net.Security;
using System.Security.Authentication;
using FluentAssertions;
using NodePilot.Cli.Output;
using NodePilot.Core.Clients;
using Spectre.Console;
using Xunit;

namespace NodePilot.Cli.Tests.Tls;

public sealed class NetworkErrorRendererTests
{
    private const string Fingerprint = "A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F90";

    [Fact]
    public void Render_HttpRequestExceptionWithInnerChain_ShowsEveryCause()
    {
        // The reported defect: only the outer message was printed, which says nothing.
        var failure = Failure(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate was rejected."));

        var text = NetworkErrorRenderer.Render(failure, "https://np.lab.local:8443");

        text.Should().Contain("The SSL connection could not be established");
        text.Should().Contain("The remote certificate was rejected.");
    }

    [Fact]
    public void Render_UntrustedChain_NamesTheCertificateAndBothRemedies()
    {
        var failure = WithObservation(Observation() with
        {
            PolicyErrors = SslPolicyErrors.RemoteCertificateChainErrors,
            ChainStatus = "UntrustedRoot",
        });

        var text = NetworkErrorRenderer.Render(failure, "https://np.lab.local:8443");

        text.Should().Contain("nicht vertrauenswürdig");
        text.Should().Contain("UntrustedRoot");
        text.Should().Contain("CN=np.lab.local");
        text.Should().Contain("np.lab.local, localhost");
        text.Should().Contain(Fingerprint);
        text.Should().Contain("--tls-thumbprint");
        text.Should().Contain(@"Cert:\LocalMachine\Root");
        text.Should().Contain("--insecure-tls");
    }

    [Fact]
    public void Render_PinMismatch_SuggestsNeitherPinningNorBypass()
    {
        var failure = WithObservation(Observation() with
        {
            PolicyErrors = SslPolicyErrors.RemoteCertificateChainErrors,
            PinConfigured = true,
            PinMatched = false,
        });

        var text = NetworkErrorRenderer.Render(failure, "https://np.lab.local:8443");

        text.Should().Contain("Pin passt nicht");
        text.Should().NotContain("--insecure-tls");
        text.Should().NotContain("np auth login --tls-thumbprint");
    }

    [Fact]
    public void Render_NameMismatch_NamesTheCertificateNames()
    {
        var failure = WithObservation(Observation() with
        {
            PolicyErrors = SslPolicyErrors.RemoteCertificateNameMismatch,
        });

        NetworkErrorRenderer.Render(failure, "https://np.lab.local:8443")
            .Should().Contain("DNS: np.lab.local, localhost");
    }

    [Fact]
    public void Render_HandshakeFailureWithoutObservation_OmitsCertificateBlock()
    {
        // No certificate was seen for this request — reporting one from elsewhere would be a lie.
        var failure = Failure(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException("Authentication failed."));

        var text = NetworkErrorRenderer.Render(failure, "https://np.lab.local:8443");

        text.Should().Contain("Protokoll oder Cipher");
        text.Should().NotContain("SHA-256");
    }

    [Fact]
    public void Render_NonTlsFailure_ReportsOnlyTheCauseChain()
    {
        var failure = Failure("No such host is known.", inner: null);

        var text = NetworkErrorRenderer.Render(failure, "https://np.lab.local:8443");

        text.Should().Contain("Verbindung zu https://np.lab.local:8443 fehlgeschlagen");
        text.Should().Contain("No such host is known.");
        text.Should().NotContain("Abhilfe");
    }

    [Fact]
    public void Render_CertificateSubjectWithMarkupCharacters_SurvivesSpectreEscaping()
    {
        var failure = WithObservation(Observation() with
        {
            Subject = "CN=np.lab.local, OU=[Ops], O=Contoso",
            PolicyErrors = SslPolicyErrors.RemoteCertificateChainErrors,
        });

        var text = NetworkErrorRenderer.Render(failure, "https://np.lab.local:8443");

        // ErrorBlock escapes the whole block once; the escaped form must still parse as markup.
        var act = () => Markup.Escape(text).Length;
        act.Should().NotThrow();
        text.Should().Contain("OU=[Ops]");
    }

    private static PresentedCertificateInfo Observation() => new()
    {
        RequestHost = "np.lab.local",
        Subject = "CN=np.lab.local",
        Issuer = "CN=np.lab.local",
        Sha256 = Fingerprint,
        DnsNames = new[] { "np.lab.local", "localhost" },
        NotBefore = DateTimeOffset.UtcNow.AddDays(-1),
        NotAfter = DateTimeOffset.UtcNow.AddYears(1),
    };

    private static HttpRequestException WithObservation(PresentedCertificateInfo observation)
    {
        var failure = Failure(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate was rejected."));
        failure.Data[TlsObservationHandler.ExceptionDataKey] = observation;
        return failure;
    }

    private static HttpRequestException Failure(string message, Exception? inner)
        => new(message, inner);
}
