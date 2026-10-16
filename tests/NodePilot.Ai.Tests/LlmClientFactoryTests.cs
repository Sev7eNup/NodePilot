using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using NodePilot.TestCommons;

namespace NodePilot.Ai.Tests;

public sealed class LlmClientFactoryTests
{
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

    // ---- Profile API key vs. per-node baseUrl ------------------------------------------

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Authorization;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        }
    }

    private static async Task<string?> AuthorizationSentAsync(LlmConnection overrides)
    {
        var options = OptionsWith(baseUrl: "https://api.openai.com/v1");
        options.Profiles["default"].ApiKey = "sk-profile";
        var handler = new CapturingHandler();
        var factory = new LlmClientFactory(
            new StubHttpClientFactory(handler), new StaticOptionsMonitor<LlmOptions>(options), NullLoggerFactory.Instance);

        var client = factory.Create(overrides);
        var act = () => client.CompleteAsync(new LlmRequest("system", "prompt"), TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<LlmException>();
        return handler.Authorization;
    }

    [Fact]
    public async Task Create_OverrideBaseUrlOnForeignOrigin_DoesNotSendProfileKey()
    {
        var sent = await AuthorizationSentAsync(new LlmConnection(BaseUrl: "https://llm.example.net/v1"));

        sent.Should().BeNull("the profile key must never travel to a host the profile does not name");
    }

    [Fact]
    public async Task Create_OverrideBaseUrlOnForeignOrigin_SendsNodeKey()
    {
        var sent = await AuthorizationSentAsync(
            new LlmConnection(BaseUrl: "https://llm.example.net/v1", ApiKey: "sk-node"));

        sent.Should().Be("Bearer sk-node");
    }

    [Theory]
    [InlineData("https://api.openai.com/v1/responses")]
    [InlineData("https://API.openai.com:443/v1")]
    public async Task Create_OverrideBaseUrlOnProfileOrigin_KeepsProfileKey(string baseUrl)
    {
        var sent = await AuthorizationSentAsync(new LlmConnection(BaseUrl: baseUrl));

        sent.Should().Be("Bearer sk-profile");
    }

    [Fact]
    public async Task Create_OverrideBaseUrlOnOtherPort_DoesNotSendProfileKey()
    {
        // Same host is not enough: a different port is a different service.
        var sent = await AuthorizationSentAsync(new LlmConnection(BaseUrl: "https://api.openai.com:8443/v1"));

        sent.Should().BeNull();
    }

    [Fact]
    public async Task Create_NoOverride_SendsProfileKey()
    {
        var sent = await AuthorizationSentAsync(new LlmConnection(Model: "gpt-4o"));

        sent.Should().Be("Bearer sk-profile");
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
