using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using NodePilot.TestCommons;

namespace NodePilot.Ai.Tests;

public sealed class LlmClientFactoryTests
{
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static LlmOptions OptionsWith(
        string baseUrl = "https://api.openai.com/v1",
        string model = "gpt-4o-mini",
        string activeProfileId = "default",
        string profileId = "default") => new()
    {
        Enabled = true,
        ActiveProfileId = activeProfileId,
        Profiles = new Dictionary<string, LlmProfileOptions>(StringComparer.OrdinalIgnoreCase)
        {
            [profileId] = new()
            {
                Name = "Test", BaseUrl = baseUrl, Model = model, MaxTokens = 4096, TimeoutSeconds = 90,
            },
        },
    };

    private static LlmClientFactory Build(LlmOptions? options = null)
        => new(
            new StubHttpClientFactory(),
            new StaticOptionsMonitor<LlmOptions>(options ?? OptionsWith()),
            NullLoggerFactory.Instance);

    [Fact]
    public void Create_ActiveProfile_ReturnsClient()
    {
        Build().Create(null).Should().NotBeNull();
    }

    [Fact]
    public void Create_ValidOverride_ReturnsClient()
    {
        Build().Create(new LlmConnection(BaseUrl: "http://localhost:11434/v1", Model: "llama3", Temperature: 0.5))
            .Should().NotBeNull();
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", typeof(OpenAiCompatibleLlmClient))]
    [InlineData("https://api.openai.com/v1/chat/completions", typeof(OpenAiCompatibleLlmClient))]
    [InlineData("https://api.openai.com/v1/responses", typeof(OpenAiResponsesLlmClient))]
    public void Create_BaseUrlPath_SelectsTheMatchingDialectClient(string baseUrl, Type expected)
    {
        Build(OptionsWith(baseUrl: baseUrl)).Create(null).Should().BeOfType(expected);
    }

    [Fact]
    public void Create_PerNodeBaseUrlOverride_SelectsTheDialectFromTheOverride()
    {
        // The llmQuery per-node override changes the endpoint, so it has to change the dialect too.
        Build(OptionsWith(baseUrl: "https://api.openai.com/v1"))
            .Create(new LlmConnection(BaseUrl: "https://api.openai.com/v1/responses"))
            .Should().BeOfType<OpenAiResponsesLlmClient>();
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
    public void Create_ActiveProfileBaseUrlMetadata_Throws()
    {
        var factory = Build(OptionsWith(baseUrl: "http://169.254.169.254/v1"));
        var act = () => factory.Create(null);
        act.Should().Throw<LlmException>();
    }

    [Fact]
    public void Create_NoProfilesConfigured_Throws()
    {
        var factory = Build(new LlmOptions { Enabled = true, ActiveProfileId = "default" });
        var act = () => factory.Create(null);
        act.Should().Throw<LlmException>().Where(e => e.Message.Contains("No active LLM profile"));
    }

    [Fact]
    public void Create_ActiveProfileIdUnknown_Throws()
    {
        // Deliberately no "just take the first one" fallback: silently talking to a different
        // endpoint than the operator selected is worse than a clear failure.
        var factory = Build(OptionsWith(activeProfileId: "gone", profileId: "default"));
        var act = () => factory.Create(null);
        act.Should().Throw<LlmException>().Where(e => e.Message.Contains("No active LLM profile"));
    }

    [Fact]
    public void Create_ActiveProfileIdEmpty_Throws()
    {
        var factory = Build(OptionsWith(activeProfileId: ""));
        var act = () => factory.Create(null);
        act.Should().Throw<LlmException>();
    }

    [Fact]
    public void Create_ActiveProfileIdIsCaseInsensitive()
    {
        Build(OptionsWith(activeProfileId: "DEFAULT", profileId: "default")).Create(null).Should().NotBeNull();
    }

    [Fact]
    public void Create_OverridesWinOverActiveProfile()
    {
        // The override path must not be blocked by an unusable profile value — but it also must not
        // bypass the guard, which the metadata test above covers.
        var factory = Build(OptionsWith(baseUrl: "https://api.openai.com/v1"));
        factory.Create(new LlmConnection(BaseUrl: "http://localhost:1234/v1")).Should().NotBeNull();
    }

    [Fact]
    public void Create_SwitchingActiveProfile_TakesEffectWithoutRestart()
    {
        // Hot-reload: LlmClientFactory reads IOptionsMonitor<LlmOptions>.CurrentValue per Create(),
        // so switching Llm:ActiveProfileId in the Settings UI takes effect without a restart. Start
        // on a safe profile, flip the active id to one with a cloud-metadata URL (SSRF-guarded) and
        // assert the very next Create() reflects it — no factory re-construction.
        var profiles = new Dictionary<string, LlmProfileOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["cloud"] = new() { Name = "Cloud", BaseUrl = "https://api.openai.com/v1", Model = "gpt-4o-mini" },
            ["evil"] = new() { Name = "Evil", BaseUrl = "http://169.254.169.254/v1", Model = "gpt-4o-mini" },
        };
        var monitor = new MutableOptionsMonitor<LlmOptions>(new LlmOptions
        {
            Enabled = true, ActiveProfileId = "cloud", Profiles = profiles,
        });
        var factory = new LlmClientFactory(new StubHttpClientFactory(), monitor, NullLoggerFactory.Instance);

        factory.Create(null).Should().NotBeNull();

        monitor.Set(new LlmOptions { Enabled = true, ActiveProfileId = "evil", Profiles = profiles });

        var act = () => factory.Create(null);
        act.Should().Throw<LlmException>().Where(e => e.Message.Contains("cloud-metadata"));
    }

    [Fact]
    public void Create_ProfileEdit_TakesEffectWithoutRestart()
    {
        var monitor = new MutableOptionsMonitor<LlmOptions>(OptionsWith());
        var factory = new LlmClientFactory(new StubHttpClientFactory(), monitor, NullLoggerFactory.Instance);

        factory.Create(null).Should().NotBeNull();

        monitor.Set(OptionsWith(baseUrl: "http://169.254.169.254/v1"));

        var act = () => factory.Create(null);
        act.Should().Throw<LlmException>().Where(e => e.Message.Contains("cloud-metadata"));
    }
}
