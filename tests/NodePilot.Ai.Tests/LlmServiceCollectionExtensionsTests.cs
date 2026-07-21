using NodePilot.Ai;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace NodePilot.Ai.Tests;

/// <summary>
/// The cloud-metadata endpoint guard is the only non-trivial logic in
/// <see cref="LlmServiceCollectionExtensions"/>. It must catch every common cloud
/// provider's instance-metadata host (used in SSRF attacks) while still letting
/// ordinary LLM endpoints (local + cloud) through.
/// </summary>
public class LlmServiceCollectionExtensionsTests
{
    [Theory]
    [InlineData("http://169.254.169.254/v1")]
    [InlineData("http://169.254.169.254:8080/v1")]
    [InlineData("https://169.254.42.7/v1")] // 169.254/16 — also covers Microsoft Azure wire-server IPs
    [InlineData("http://metadata.google.internal/v1")]
    [InlineData("https://metadata.azure.com/v1")]
    [InlineData("http://METADATA.GOOGLE.INTERNAL/v1")] // case-insensitive
    public void IsCloudMetadataEndpoint_KnownProviders_ReturnsTrue(string url)
    {
        LlmEndpointGuard.IsCloudMetadataEndpoint(url).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("http://127.0.0.1:8080/v1")]
    [InlineData("http://10.0.0.5/v1")]
    [InlineData("https://my-llm.internal.example.com/v1")]
    [InlineData("http://[::1]:8080/v1")]
    [InlineData("not-a-url-at-all")] // an unparseable string must NOT trip fail-fast (an operator typo isn't an SSRF attempt)
    public void IsCloudMetadataEndpoint_LegitimateUrls_ReturnsFalse(string url)
    {
        LlmEndpointGuard.IsCloudMetadataEndpoint(url).Should().BeFalse();
    }

    // ---- AddNodePilotAi --------------------------------------------------------------

    private static IServiceCollection NewServices(Dictionary<string, string?> overrides)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(overrides).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNodePilotAi(config);
        return services;
    }

    [Fact]
    public void AddNodePilotAi_RegistersAllServices()
    {
        var services = NewServices(new Dictionary<string, string?>
        {
            ["Llm:Enabled"] = "false",
            ["Llm:BaseUrl"] = "http://localhost:11434/v1",
        });

        // Smoke check: all three service bindings are registered.
        services.Should().Contain(d => d.ServiceType == typeof(ILlmClient));
        services.Should().Contain(d => d.ServiceType == typeof(ScriptGenerationService));
        services.Should().Contain(d => d.ServiceType == typeof(WorkflowGenerationService));
        services.Should().Contain(d => d.ServiceType == typeof(PromptCatalog));
    }

    [Fact]
    public void AddNodePilotAi_BuildsServiceProviderAndResolvesAllDependencies()
    {
        var services = NewServices(new Dictionary<string, string?>
        {
            ["Llm:Enabled"] = "false",
            ["Llm:BaseUrl"] = "http://localhost:11434/v1",
            ["Llm:Model"] = "test-model",
        });

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();

        // If the DI graph doesn't wire up correctly (e.g. a missing registration),
        // resolving here throws — this catches wiring mistakes in AddNodePilotAi during a refactor.
        scope.ServiceProvider.GetRequiredService<ILlmClient>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<ScriptGenerationService>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<WorkflowGenerationService>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<PromptCatalog>().Should().NotBeNull();
    }

    [Fact]
    public void AddNodePilotAi_LlmHttpClient_DisablesOwnTimeout_SoLinkedCtsGoverns()
    {
        // Regression guard (slow local LLM): the per-request timeout is enforced by
        // OpenAiCompatibleLlmClient via a linked CancellationTokenSource (CancelAfter
        // Llm:TimeoutSeconds). If the named HttpClient kept the .NET default of 100s, a slow
        // local model (>100s) would get cut off at 100s — surfacing a misleading "LLM endpoint
        // did not respond within {TimeoutSeconds}s" error even though TimeoutSeconds was
        // configured much higher (e.g. 3600s). So the named client MUST disable its own timeout.
        var services = NewServices(new Dictionary<string, string?>
        {
            ["Llm:Enabled"] = "false",
            ["Llm:BaseUrl"] = "http://127.0.0.1:1234/v1",
        });
        using var sp = services.BuildServiceProvider();

        using var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(LlmHttpClient.Name);

        client.Timeout.Should().Be(Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public void AddNodePilotAi_EnabledWithMetadataEndpoint_FailsFast()
    {
        var act = () => NewServices(new Dictionary<string, string?>
        {
            ["Llm:Enabled"] = "true",
            ["Llm:BaseUrl"] = "http://169.254.169.254/v1",
        });

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*cloud-metadata*");
    }

    [Fact]
    public void AddNodePilotAi_DisabledWithMetadataEndpoint_DoesNotThrow()
    {
        // The operator left Llm:Enabled=false in place — the fail-fast path must not trigger
        // here, otherwise an accidentally configured metadata IP could crash the whole boot
        // process even though the feature isn't actually active.
        var act = () => NewServices(new Dictionary<string, string?>
        {
            ["Llm:Enabled"] = "false",
            ["Llm:BaseUrl"] = "http://169.254.169.254/v1",
        });

        act.Should().NotThrow();
    }
}
