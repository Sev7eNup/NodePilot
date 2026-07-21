using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using NodePilot.Engine.Options;
using NodePilot.Engine.Security;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

/// <summary>
/// RestApiHttpClientProvider — proxy/noProxy resolution. Covers global default from
/// RestApi:Proxy:* and per-step overrides (proxyMode direct/custom with noProxy bypass).
/// </summary>
public class RestApiProxyTests
{
    private static RestApiProxyOptions BuildOpts(bool enabled = false, string? address = null) =>
        new() { Enabled = enabled, Address = address };

    private static JsonElement ParseConfig(string json)
        => JsonDocument.Parse(json).RootElement;

    /// <summary>Empty IConfiguration — flips the SSRF guard back into the default-on policy
    /// (link-local always blocked, RFC1918 blocked because the missing key reads as "true").
    /// Tests that exercise proxy/handler wiring don't hit the network so the policy never
    /// fires, but the constructor and BuildDefaultHandler now require a non-null instance.</summary>
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().Build();

    private static RestApiHttpClientProvider CreateProvider()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
        return new RestApiHttpClientProvider(factory.Object, EmptyConfig());
    }

    [Fact]
    public void GlobalProxy_Disabled_UsesDirectConnection()
    {
        using var handler = RestApiHttpClientProvider.BuildDefaultHandler(BuildOpts(enabled: false), EmptyConfig());

        handler.UseProxy.Should().BeFalse();
        handler.Proxy.Should().BeNull();
    }

    [Fact]
    public void GlobalProxy_Enabled_AppliedToDefaultHandler()
    {
        using var handler = RestApiHttpClientProvider.BuildDefaultHandler(
            BuildOpts(enabled: true, address: "http://proxy.corp.local:8080"),
            EmptyConfig());

        handler.UseProxy.Should().BeTrue();
        handler.Proxy.Should().NotBeNull();
        var webProxy = handler.Proxy as WebProxy;
        webProxy.Should().NotBeNull();
        webProxy!.Address.Should().Be(new Uri("http://proxy.corp.local:8080"));
    }

    [Fact]
    public void GlobalProxy_EnabledWithoutAddress_ThrowsAtStartup()
    {
        var act = () => RestApiHttpClientProvider.BuildDefaultHandler(BuildOpts(enabled: true), EmptyConfig());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Enabled is true but*Address is empty*");
    }

    [Fact]
    public void StepProxyMode_Direct_DisablesProxyRegardlessOfGlobal()
    {
        var provider = CreateProvider();
        var stepConfig = ParseConfig("""{"url":"https://x","proxyMode":"direct"}""");

        var handler = provider.ResolveOverrideHandler(stepConfig);

        handler.UseProxy.Should().BeFalse();
        handler.Proxy.Should().BeNull();
    }

    [Fact]
    public void StepProxyMode_Custom_UsesStepLocalProxy()
    {
        var provider = CreateProvider();
        var stepConfig = ParseConfig("""
            {
              "url":"https://x",
              "proxyMode":"custom",
              "proxyAddress":"http://proxy.other:3128"
            }
            """);

        var handler = provider.ResolveOverrideHandler(stepConfig);

        handler.UseProxy.Should().BeTrue();
        var webProxy = handler.Proxy as WebProxy;
        webProxy!.Address.Should().Be(new Uri("http://proxy.other:3128"));
    }

    [Fact]
    public void StepProxyMode_Custom_MissingAddress_Throws()
    {
        var provider = CreateProvider();
        var stepConfig = ParseConfig("""{"url":"https://x","proxyMode":"custom"}""");

        var act = () => provider.ResolveOverrideHandler(stepConfig);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*proxyMode=\"custom\" requires a non-empty proxyAddress*");
    }

    [Fact]
    public void NoProxy_WildcardMatch_BypassesProxyForMatchingHost()
    {
        var provider = CreateProvider();
        var stepConfig = ParseConfig("""
            {
              "url":"https://x",
              "proxyMode":"custom",
              "proxyAddress":"http://proxy.corp:8080",
              "noProxy":"*.internal, localhost"
            }
            """);

        var handler = provider.ResolveOverrideHandler(stepConfig);
        var webProxy = (WebProxy)handler.Proxy!;

        // Matching wildcard → bypassed (no proxy used)
        webProxy.IsBypassed(new Uri("https://api.internal/path")).Should().BeTrue();
        // Literal match → bypassed
        webProxy.IsBypassed(new Uri("http://localhost:5000")).Should().BeTrue();
        // Non-matching → goes through proxy
        webProxy.IsBypassed(new Uri("https://api.public.com/path")).Should().BeFalse();
    }

    [Fact]
    public void DefaultProxy_DestinationPolicy_RequiresExactAllowlist()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["RestApi:Proxy:Enabled"] = "true",
                ["RestApi:Proxy:Address"] = "http://proxy.corp:8080",
            }).Build();
        var factory = new Mock<IHttpClientFactory>();
        var provider = new RestApiHttpClientProvider(factory.Object, config);

        var act = () => provider.ValidateDestinationPolicy(
            ParseConfig("{\"proxyMode\":\"default\"}"),
            new Uri("https://192.0.2.30/path"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*not explicitly allowed*");
    }

    [Fact]
    public void DefaultProxy_NoProxyBypass_UsesDirectPolicyWithoutAllowlist()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["RestApi:Proxy:Enabled"] = "true",
                ["RestApi:Proxy:Address"] = "http://proxy.corp:8080",
                ["RestApi:Proxy:BypassList:0"] = "*.internal",
            }).Build();
        var factory = new Mock<IHttpClientFactory>();
        var provider = new RestApiHttpClientProvider(factory.Object, config);
        var target = new Uri("https://api.internal/path");

        provider.UsesProxyForDestination(ParseConfig("{}"), target).Should().BeFalse();
        var act = () => provider.ValidateDestinationPolicy(ParseConfig("{}"), target);
        act.Should().NotThrow();
    }

    [Fact]
    public void CustomProxy_DestinationPolicy_TracksPerStepNoProxyAndDirectModes()
    {
        var provider = CreateProvider();
        var custom = ParseConfig("""
            {
              "proxyMode":"custom",
              "proxyAddress":"http://proxy.corp:8080",
              "noProxy":"*.internal"
            }
            """);

        var proxied = () => provider.ValidateDestinationPolicy(
            custom, new Uri("https://api.public.test/path"));
        var bypassed = () => provider.ValidateDestinationPolicy(
            custom, new Uri("https://api.internal/path"));
        var direct = () => provider.ValidateDestinationPolicy(
            ParseConfig("{\"proxyMode\":\"direct\"}"),
            new Uri("https://api.public.test/path"));

        proxied.Should().Throw<InvalidOperationException>().WithMessage("*not explicitly allowed*");
        bypassed.Should().NotThrow();
        direct.Should().NotThrow();
    }

    [Fact]
    public void HandlerCache_SameSignature_ReturnsSameInstance()
    {
        var provider = CreateProvider();
        var json = """
            {
              "proxyMode":"custom",
              "proxyAddress":"http://p:8080",
              "noProxy":"*.internal"
            }
            """;

        var a = provider.ResolveOverrideHandler(ParseConfig(json));
        var b = provider.ResolveOverrideHandler(ParseConfig(json));

        b.Should().BeSameAs(a);
    }

    [Fact]
    public void HandlerCache_DifferentSignatures_ReturnDifferentInstances()
    {
        var provider = CreateProvider();
        var a = provider.ResolveOverrideHandler(
            ParseConfig("""{"proxyMode":"custom","proxyAddress":"http://p1:8080"}"""));
        var b = provider.ResolveOverrideHandler(
            ParseConfig("""{"proxyMode":"custom","proxyAddress":"http://p2:8080"}"""));

        b.Should().NotBeSameAs(a);
    }

    [Fact]
    public void ConvertBypassToRegex_HandlesWildcardsAndLiterals()
    {
        // WebProxy.BypassList matches patterns against the full URI; the helper wraps the
        // host pattern with scheme/port/path suffixes so plain hostnames still bypass.
        RestApiHttpClientProvider.ConvertBypassToRegex("*.internal")
            .Should().Be(@"^https?://.*\.internal(:\d+)?(/.*)?$");
        RestApiHttpClientProvider.ConvertBypassToRegex("localhost")
            .Should().Be(@"^https?://localhost(:\d+)?(/.*)?$");
        RestApiHttpClientProvider.ConvertBypassToRegex("10.0.0.1")
            .Should().Be(@"^https?://10\.0\.0\.1(:\d+)?(/.*)?$");
    }

    [Fact]
    public void StepProxyMode_InvalidAddress_Throws()
    {
        var provider = CreateProvider();
        var stepConfig = ParseConfig("""
            {
              "proxyMode":"custom",
              "proxyAddress":"file:///etc/passwd"
            }
            """);

        var act = () => provider.ResolveOverrideHandler(stepConfig);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not a valid http(s) URL*");
    }
}
