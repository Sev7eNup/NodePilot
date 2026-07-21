using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodePilot.Ai;
using Xunit;
using NodePilot.TestCommons;

namespace NodePilot.Ai.Tests;

public sealed class LlmClientFactoryTests
{
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static LlmClientFactory Build(LlmOptions? global = null)
        => new(
            new StubHttpClientFactory(),
            new StaticOptionsMonitor<LlmOptions>(global ?? new LlmOptions
            {
                Enabled = true,
                BaseUrl = "https://api.openai.com/v1",
                Model = "gpt-4o-mini",
                MaxTokens = 4096,
                TimeoutSeconds = 90,
            }),
            NullLoggerFactory.Instance);

    [Fact]
    public void Create_GlobalDefault_ReturnsClient()
    {
        Build().Create(null).Should().NotBeNull();
    }

    [Fact]
    public void Create_ValidOverride_ReturnsClient()
    {
        Build().Create(new LlmConnection(BaseUrl: "http://localhost:11434/v1", Model: "llama3", Temperature: 0.5))
            .Should().NotBeNull();
    }

    [Fact]
    public void Create_MetadataOverride_ThrowsFromGuard()
    {
        var act = () => Build().Create(new LlmConnection(BaseUrl: "http://169.254.169.254/v1"));
        act.Should().Throw<LlmException>().Where(e => e.Message.Contains("cloud-metadata"));
    }

    [Fact]
    public void Create_InvalidOverrideUrl_Throws()
    {
        var act = () => Build().Create(new LlmConnection(BaseUrl: "notaurl"));
        act.Should().Throw<LlmException>();
    }

    [Fact]
    public void Create_GlobalBaseUrlMetadata_Throws()
    {
        var factory = Build(new LlmOptions { Enabled = true, BaseUrl = "http://169.254.169.254/v1", Model = "m" });
        var act = () => factory.Create(null);
        act.Should().Throw<LlmException>();
    }

    [Fact]
    public void Create_GlobalBaseUrl_FlipsLiveAfterConfigReload()
    {
        // Hot-reload: LlmClientFactory reads IOptionsMonitor<LlmOptions>.CurrentValue per Create(),
        // so changing Llm:BaseUrl in the Settings UI takes effect without a restart. Start with a
        // safe global endpoint, mutate the monitor to a cloud-metadata URL (SSRF-guarded) and
        // assert the very next Create() reflects the new value — no factory re-construction.
        var monitor = new MutableOptionsMonitor<LlmOptions>(new LlmOptions
        {
            Enabled = true,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o-mini",
        });
        var factory = new LlmClientFactory(new StubHttpClientFactory(), monitor, NullLoggerFactory.Instance);

        // Baseline: global endpoint is fine.
        factory.Create(null).Should().NotBeNull();

        // Operator rewrites Llm:BaseUrl to a metadata IP in the Settings UI → config reload.
        monitor.Set(new LlmOptions { Enabled = true, BaseUrl = "http://169.254.169.254/v1", Model = "gpt-4o-mini" });

        // Same factory instance: the next Create() now hits the SSRF guard with the live BaseUrl.
        var act = () => factory.Create(null);
        act.Should().Throw<LlmException>().Where(e => e.Message.Contains("cloud-metadata"));
    }
}
