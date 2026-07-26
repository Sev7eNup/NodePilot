using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Ai;
using NodePilot.Api.Dtos.Settings;
using NodePilot.Api.Security.Ldap;
using NodePilot.Api.Services;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Services;

/// <summary>
/// The Admin-Settings "test connection" probes. Every probe must return a
/// <see cref="SettingsTestProbeResult"/> — never throw — because the UI renders the failure
/// message verbatim; an unhandled exception would surface as a 500 with no operator guidance.
/// The LLM path runs against a stub handler, the LDAP path exercises the guards that reject a
/// configuration before any socket is opened.
/// </summary>
public sealed class SettingsTestProbeTests
{
    // ---------------------------------------------------------------- argument guards

    [Fact]
    public async Task TestSmtpAsync_NullRequest_Throws()
    {
        var act = () => Probe().TestSmtpAsync(null!, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task TestSmtpAsync_WithoutSettings_Throws()
    {
        var act = () => Probe().TestSmtpAsync(
            new SmtpTestProbeRequest(null!, null), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TestLlmAsync_NullRequest_Throws()
    {
        var act = () => Probe().TestLlmAsync(null!, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task TestLdapAsync_NullRequest_Throws()
    {
        var act = () => Probe().TestLdapAsync(null!, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ---------------------------------------------------------------- SMTP

    [Fact]
    public async Task TestSmtpAsync_UnreachableHost_ReportsFailureInsteadOfThrowing()
    {
        // Port 1 on loopback is reliably closed — the probe must translate the connect
        // error into a result the settings modal can render.
        var result = await Probe().TestSmtpAsync(
            new SmtpTestProbeRequest(
                new SmtpSettingsDto { Host = "127.0.0.1", Port = 1, From = "np@example.test" },
                "ops@example.test"),
            TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        result.Message.Should().StartWith("SMTP probe failed:");
        result.ErrorKind.Should().NotBeNullOrWhiteSpace();
    }

    // ---------------------------------------------------------------- LLM

    [Fact]
    public async Task TestLlmAsync_EndpointAccepts_ReportsSuccess()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"data\":[]}");

        var result = await Probe(handler).TestLlmAsync(
            new LlmTestProbeRequest(Llm("http://127.0.0.1:1234/v1")),
            TestContext.Current.CancellationToken);

        result.Ok.Should().BeTrue();
        handler.LastRequest!.RequestUri!.ToString().Should().EndWith("/models",
            "the probe uses the cheap model listing rather than a chat completion");
    }

    [Fact]
    public async Task TestLlmAsync_SendsTheApiKeyAsABearerToken()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");

        await Probe(handler).TestLlmAsync(
            new LlmTestProbeRequest(Llm("http://127.0.0.1:1234/v1", apiKey: "sk-secret")),
            TestContext.Current.CancellationToken);

        handler.LastRequest!.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.Should().Be("sk-secret");
    }

    [Fact]
    public async Task TestLlmAsync_WithoutApiKey_SendsNoAuthorizationHeader()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");

        await Probe(handler).TestLlmAsync(
            new LlmTestProbeRequest(Llm("http://127.0.0.1:1234/v1", apiKey: null)),
            TestContext.Current.CancellationToken);

        handler.LastRequest!.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task TestLlmAsync_NonSuccessStatus_ReportsTheStatusAndBody()
    {
        var handler = new StubHandler(HttpStatusCode.Unauthorized, "invalid api key");

        var result = await Probe(handler).TestLlmAsync(
            new LlmTestProbeRequest(Llm("http://127.0.0.1:1234/v1")),
            TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("401").And.Contain("invalid api key");
        result.ErrorKind.Should().Be("Unauthorized");
    }

    [Fact]
    public async Task TestLlmAsync_LongErrorBody_IsTruncated()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, new string('x', 500));

        var result = await Probe(handler).TestLlmAsync(
            new LlmTestProbeRequest(Llm("http://127.0.0.1:1234/v1")),
            TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("…", "an endpoint dumping a 500-char body must not flood the modal");
        result.Message.Length.Should().BeLessThan(400);
    }

    [Fact]
    public async Task TestLlmAsync_TransportFailure_ReportsFailureInsteadOfThrowing()
    {
        var handler = new ThrowingHandler(new HttpRequestException("connection refused"));

        var result = await Probe(handler).TestLlmAsync(
            new LlmTestProbeRequest(Llm("http://127.0.0.1:1234/v1")),
            TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("connection refused");
        result.ErrorKind.Should().Be(nameof(HttpRequestException));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.test/v1")]
    [InlineData("http://169.254.169.254/latest")]
    public async Task TestLlmAsync_BaseUrlRejectedByTheGuard_NeverReachesTheTransport(string baseUrl)
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");

        var result = await Probe(handler).TestLlmAsync(
            new LlmTestProbeRequest(Llm(baseUrl)), TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        handler.LastRequest.Should().BeNull(
            "the shared LlmEndpointGuard rejects the URL before any connect is attempted");
    }

    // ---------------------------------------------------------------- LDAP guards

    [Fact]
    public async Task TestLdapAsync_WithoutLdaps_IsRefused()
    {
        var result = await Probe().TestLdapAsync(
            new LdapTestProbeRequest(new LdapAuthenticationDto { UseSsl = false }),
            TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        result.ErrorKind.Should().Be("TlsRequired");
    }

    [Fact]
    public async Task TestLdapAsync_WithoutServiceBindCredentials_IsRefused()
    {
        var result = await Probe().TestLdapAsync(
            new LdapTestProbeRequest(new LdapAuthenticationDto
            {
                UseSsl = true, Server = "dc.example.test", ServiceBindDn = null,
            }),
            TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        result.ErrorKind.Should().Be("ServiceBindRequired",
            "sync and offboarding both need a service bind, so the probe must not pass without one");
    }

    [Fact]
    public async Task TestLdapAsync_MaskedPasswordFallsBackToTheStoredSecret()
    {
        // The settings UI sends "********" for an unchanged secret. Without the fallback the
        // probe would report ServiceBindRequired even though a password is configured.
        var stored = new LdapOptions { ServicePassword = "" };
        var result = await Probe(ldapOptions: stored).TestLdapAsync(
            new LdapTestProbeRequest(new LdapAuthenticationDto
            {
                UseSsl = true,
                Server = "dc.example.test",
                ServiceBindDn = "CN=svc,DC=example,DC=test",
                ServicePassword = "********",
            }),
            TestContext.Current.CancellationToken);

        result.ErrorKind.Should().Be("ServiceBindRequired",
            "an empty stored secret is still no secret — the placeholder must not paper over it");
    }

    [Fact]
    public async Task TestLdapAsync_WithoutAnyEndpoint_ReportsConfigurationError()
    {
        var result = await Probe().TestLdapAsync(
            new LdapTestProbeRequest(new LdapAuthenticationDto
            {
                UseSsl = true,
                Server = null,
                Endpoints = [],
                ServiceBindDn = "CN=svc,DC=example,DC=test",
                ServicePassword = "secret",
            }),
            TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        result.ErrorKind.Should().Be("Configuration");
    }

    [Fact]
    public async Task TestLdapAsync_UnreachableEndpoint_ReportsPerEndpointFailure()
    {
        var result = await Probe().TestLdapAsync(
            new LdapTestProbeRequest(new LdapAuthenticationDto
            {
                UseSsl = true,
                Server = "127.0.0.1",
                Port = 1,
                BindTimeoutSeconds = 1,
                BaseDn = "DC=example,DC=test",
                ServiceBindDn = "CN=svc,DC=example,DC=test",
                ServicePassword = "secret",
            }),
            TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("127.0.0.1:1",
            "the operator needs to see which endpoint failed, not just that something did");
    }

    // ---------------------------------------------------------------- helpers

    private static LlmSettingsDto Llm(string baseUrl, string? apiKey = null) => new()
    {
        Enabled = true,
        BaseUrl = baseUrl,
        Model = "test-model",
        ApiKey = apiKey,
        TimeoutSeconds = 5,
    };

    private static SettingsTestProbe Probe(
        HttpMessageHandler? handler = null,
        LdapOptions? ldapOptions = null) => new(
        NullLogger<SettingsTestProbe>.Instance,
        new StubHttpClientFactory(handler ?? new StubHandler(HttpStatusCode.OK, "{}")),
        ldapOptions is null ? null : new StaticOptionsMonitor<LdapOptions>(ldapOptions));

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            });
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => throw exception;
    }
}
