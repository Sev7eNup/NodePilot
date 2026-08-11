using System.Net;
using System.Security.Authentication;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NodePilot.Ai.Tests;

/// <summary>
/// Every way of failing to reach an LLM endpoint used to arrive as the same sentence — "LLM
/// endpoint did not respond within {TimeoutSeconds}s" — because one budget covered DNS, TCP, TLS
/// and the model's answer alike. With a profile at 360 s that meant six minutes of silence and
/// then a message that pointed at the model when the real cause was a firewall or an untrusted
/// certificate.
///
/// <para>These tests pin the split: each stage now fails on its own deadline and says which one it
/// was. They assert the <i>stage naming</i>, not the exact prose — the wording is allowed to
/// improve, the distinction is not allowed to collapse again.</para>
/// </summary>
public sealed class LlmUnreachableDiagnosticsTests
{
    private static LlmHttpTransport Transport() => new(
        new StubHttpClientFactory(),
        new LlmClientConfig(
            Endpoint: LlmEndpointGuard.ResolveEndpoint("https://llm.example.intern/v1"),
            ApiKey: null,
            Model: "test-model",
            MaxTokens: 100,
            Temperature: null,
            TimeoutSeconds: 360),
        NullLogger<LlmHttpTransport>.Instance);

    [Fact]
    public void DescribeUnreachable_CertificateRejected_PointsAtTheMachineTrustStore()
    {
        // The case a corporate endpoint hits: the CA is trusted on the admin's workstation but was
        // never rolled out to the host the service runs on.
        var ex = new HttpRequestException("An error occurred while sending the request.",
            new AuthenticationException("The remote certificate is invalid according to the validation procedure."));

        var message = Transport().DescribeUnreachable(ex);

        message.Should().Contain("TLS");
        message.Should().Contain("certificate");
        message.Should().Contain("Trusted Root",
            "the operator needs to be told where to put the CA, not merely that TLS failed");
    }

    [Fact]
    public void DescribeUnreachable_HandshakeTimeout_IsNotReportedAsASlowModel()
    {
        // SocketsHttpHandler.ConnectTimeout fired. DNS and TCP carry shorter deadlines of their
        // own, so this can only be the handshake — an endpoint demanding a client certificate, an
        // SNI mismatch, or a middlebox that accepts the socket and never negotiates.
        var ex = new HttpRequestException("An error occurred while sending the request.",
            new TimeoutException("A connection could not be established within the configured ConnectTimeout."));

        var message = Transport().DescribeUnreachable(ex);

        message.Should().Contain("TLS handshake");
        message.Should().NotContain("model",
            "blaming the model is exactly the misdiagnosis this split exists to prevent");
    }

    [Theory]
    [InlineData("LLM endpoint DNS: resolving 'llm.example.intern' did not finish within 15s.")]
    [InlineData("LLM endpoint TCP: no answer from llm.example.intern:443 within 15s (tried 10.0.0.5).")]
    public void DescribeUnreachable_StageFromTheConnectGuard_IsPassedThroughVerbatim(string guardMessage)
    {
        // The guard already names its stage; wrapping it in a second, vaguer sentence would only
        // bury it.
        var ex = new HttpRequestException("An error occurred while sending the request.",
            new IOException(guardMessage));

        Transport().DescribeUnreachable(ex).Should().Be(guardMessage);
    }

    [Fact]
    public void DescribeUnreachable_UnclassifiedFailure_StillNamesTheEndpoint()
    {
        var ex = new HttpRequestException("An error occurred while sending the request.",
            new IOException("The response ended prematurely."));

        var message = Transport().DescribeUnreachable(ex);

        message.Should().Contain("unreachable");
        message.Should().Contain("llm.example.intern", "the URL is what the operator checks first");
        message.Should().Contain("The response ended prematurely.",
            "the innermost message carries the detail; HttpRequestException's own is generic");
    }

    [Fact]
    public void HandshakeTimeout_ExceedsTheConnectPhaseBudget()
    {
        // The ordering is load-bearing: DescribeUnreachable concludes "TLS" from the fact that a
        // ConnectTimeout can only fire once DNS and TCP have already passed their own, shorter
        // deadline. If these two ever cross, that inference silently becomes wrong.
        LlmConnectGuard.HandshakeTimeout.Should().BeGreaterThan(LlmConnectGuard.ConnectPhaseTimeout);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
