using System.Net.Security;
using System.Security.Authentication;
using FluentAssertions;
using ModelContextProtocol;
using NodePilot.Core.Clients;
using NodePilot.Mcp.Mapping;
using Xunit;

namespace NodePilot.Mcp.Tests.Mapping;

/// <summary>
/// The transport message is what an agent acts on, so it must name only remedies that address the
/// diagnosed cause. DescribeTransport is private; the message is asserted through Guard.
/// </summary>
public sealed class ApiErrorMapperTlsTests
{
    private const string Fingerprint = "A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F90";

    [Fact]
    public async Task Guard_NameMismatch_NamesTheServerUrlFromTheCertificate()
    {
        var message = await MessageFor(Observation() with
        {
            RequestHost = "localhost",
            PolicyErrors = SslPolicyErrors.RemoteCertificateNameMismatch,
        });

        message.Should().Contain("NODEPILOT_MCP_SERVER=https://np.lab.local:8443");
        message.Should().Contain("does not fix a name mismatch");
        message.Should().NotContain("Trust it by importing");
    }

    [Fact]
    public async Task Guard_UntrustedChain_KeepsTheTrustAndPinGuidance()
    {
        var message = await MessageFor(Observation() with
        {
            PolicyErrors = SslPolicyErrors.RemoteCertificateChainErrors,
        });

        message.Should().Contain(@"LocalMachine\Root");
        message.Should().Contain("NODEPILOT_MCP_TLS_THUMBPRINT");
        message.Should().NotContain("NODEPILOT_MCP_SERVER");
    }

    [Fact]
    public async Task Guard_PinMismatch_SuggestsNeitherPinningNorTrusting()
    {
        var message = await MessageFor(Observation() with
        {
            PolicyErrors = SslPolicyErrors.RemoteCertificateChainErrors,
            PinConfigured = true,
            PinMatched = false,
        });

        message.Should().Contain("does not match");
        message.Should().NotContain(@"LocalMachine\Root");
        message.Should().NotContain("NODEPILOT_MCP_SERVER");
    }

    private static async Task<string> MessageFor(PresentedCertificateInfo observation)
    {
        var failure = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate was rejected."));
        failure.Data[TlsObservationHandler.ExceptionDataKey] = observation;

        var act = () => ApiErrorMapper.Guard<bool>(() => throw failure);

        return (await act.Should().ThrowAsync<McpException>()).Which.Message;
    }

    private static PresentedCertificateInfo Observation() => new()
    {
        RequestHost = "np.lab.local",
        RequestPort = 8443,
        Subject = "CN=np.lab.local",
        Issuer = "CN=np.lab.local",
        Sha256 = Fingerprint,
        DnsNames = new[] { "np.lab.local" },
        NotBefore = DateTimeOffset.UtcNow.AddDays(-1),
        NotAfter = DateTimeOffset.UtcNow.AddYears(1),
    };
}
