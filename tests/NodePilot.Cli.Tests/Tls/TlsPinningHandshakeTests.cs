using FluentAssertions;
using NodePilot.Core.Clients;
using Xunit;

namespace NodePilot.Cli.Tests.Tls;

/// <summary>
/// Exercises the handler against a real handshake. The decision matrix is covered by
/// <see cref="PinnedCertificateValidatorTests"/>; these tests prove the callback is reached through
/// HttpClientHandler and that an observation ends up on the request that caused it.
/// </summary>
public sealed class TlsPinningHandshakeTests
{
    [Fact]
    public async Task HttpClient_SelfSignedListenerWithMatchingPin_CompletesRequest()
    {
        var certificate = TestCertificates.CreateSelfSigned("localhost", "localhost");
        var pin = CertificatePin.Compute(certificate);
        using var server = new TestTlsServer(certificate);
        using var http = CreateClient(server, new ClientTlsOptions(pin));

        var response = await http.GetAsync("api/ping", TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public async Task HttpClient_SelfSignedListenerWithoutPin_FailsAndReportsThePresentedCertificate()
    {
        var certificate = TestCertificates.CreateSelfSigned("localhost", "localhost");
        var expected = CertificatePin.Compute(certificate);
        using var server = new TestTlsServer(certificate);
        using var http = CreateClient(server, ClientTlsOptions.None);

        var act = () => http.GetAsync("api/ping", TestContext.Current.CancellationToken);

        var failure = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        var info = NetworkFailureAnalyzer.Analyze(failure);
        info.Tls.Should().Be(TlsFailureKind.UntrustedChain);
        info.Certificate!.Sha256.Should().Be(expected);
        info.CauseChain.Should().HaveCountGreaterThan(1, "the inner exception carries the actual cause");
    }

    [Fact]
    public async Task HttpClient_SelfSignedListenerWithWrongPin_ReportsPinMismatch()
    {
        var certificate = TestCertificates.CreateSelfSigned("localhost", "localhost");
        using var server = new TestTlsServer(certificate);
        using var http = CreateClient(server, new ClientTlsOptions(new string('A', 64)));

        var act = () => http.GetAsync("api/ping", TestContext.Current.CancellationToken);

        var failure = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        NetworkFailureAnalyzer.Analyze(failure).Tls.Should().Be(TlsFailureKind.PinMismatch);
    }

    [Fact]
    public async Task HttpClient_HandshakeFailsAfterEarlierSuccessToSameHost_OmitsCertificate()
    {
        // The earlier handshake succeeded, so a shared "last observation" slot would still hold its
        // certificate and the next failure would be explained with the wrong one.
        var certificate = TestCertificates.CreateSelfSigned("localhost", "localhost");
        var pin = CertificatePin.Compute(certificate);
        using var server = new TestTlsServer(certificate);
        using var http = CreateClient(server, new ClientTlsOptions(pin));

        (await http.GetAsync("api/ping", TestContext.Current.CancellationToken)).IsSuccessStatusCode.Should().BeTrue();
        server.DropConnections = true;

        var act = () => http.GetAsync("api/ping", TestContext.Current.CancellationToken);

        var failure = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        var info = NetworkFailureAnalyzer.Analyze(failure);
        info.Certificate.Should().BeNull();
    }

    [Fact]
    public async Task HttpClient_ConcurrentRequests_AttachTheirOwnObservation()
    {
        var first = TestCertificates.CreateSelfSigned("localhost", "localhost");
        var second = TestCertificates.CreateSelfSigned("localhost", "localhost");
        var firstPin = CertificatePin.Compute(first);
        var secondPin = CertificatePin.Compute(second);
        using var firstServer = new TestTlsServer(first);
        using var secondServer = new TestTlsServer(second);

        for (var i = 0; i < 5; i++)
        {
            // Both clients pin the certificate of the *other* server, so both fail and each failure
            // must carry the certificate its own server presented.
            using var toFirst = CreateClient(firstServer, new ClientTlsOptions(secondPin));
            using var toSecond = CreateClient(secondServer, new ClientTlsOptions(firstPin));

            var failures = await Task.WhenAll(
                CaptureFailureAsync(toFirst),
                CaptureFailureAsync(toSecond));

            NetworkFailureAnalyzer.Analyze(failures[0]).Certificate!.Sha256.Should().Be(firstPin);
            NetworkFailureAnalyzer.Analyze(failures[1]).Certificate!.Sha256.Should().Be(secondPin);
        }
    }

    private static async Task<Exception> CaptureFailureAsync(HttpClient http)
    {
        try
        {
            await http.GetAsync("api/ping", TestContext.Current.CancellationToken);
            throw new InvalidOperationException("The request was expected to fail.");
        }
        catch (HttpRequestException ex)
        {
            return ex;
        }
    }

    private static HttpClient CreateClient(TestTlsServer server, ClientTlsOptions tls)
        => new(PinnedCertificateHandlerFactory.Create(tls), disposeHandler: true)
        {
            BaseAddress = server.BaseAddress,
            Timeout = TimeSpan.FromSeconds(15),
        };
}
