using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NodePilot.Ai;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Activities;
using Xunit;
using NodePilot.TestCommons;

namespace NodePilot.Engine.Tests.Activities;

public sealed class LlmQueryActivityTests
{
    private sealed class StubLlmClient : ILlmClient
    {
        private readonly Func<LlmRequest, Task<LlmResponse>> _complete;
        public LlmRequest? LastRequest { get; private set; }
        public StubLlmClient(Func<LlmRequest, Task<LlmResponse>> complete) => _complete = complete;

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return _complete(request);
        }

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request, CancellationToken ct)
            => throw new NotSupportedException("llmQuery uses CompleteAsync, not streaming.");
    }

    private sealed class StubFactory : ILlmClientFactory
    {
        private readonly ILlmClient _client;
        public LlmConnection? LastOverrides { get; private set; }
        public bool Created { get; private set; }
        public StubFactory(ILlmClient client) => _client = client;

        public ILlmClient Create(LlmConnection? overrides = null)
        {
            LastOverrides = overrides;
            Created = true;
            return _client;
        }
    }

    private static StepExecutionContext Ctx() => new() { WorkflowExecutionId = Guid.NewGuid(), StepId = "step-1" };

    private static JsonElement Cfg(object o) => JsonSerializer.SerializeToElement(o);

    private static (LlmQueryActivity activity, StubFactory factory) Build(
        ILlmClient client, bool enabled = true)
    {
        var factory = new StubFactory(client);
        var options = new StaticOptionsMonitor<LlmOptions>(new LlmOptions
        {
            Enabled = enabled,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o-mini",
            MaxTokens = 4096,
            TimeoutSeconds = 90,
        });
        return (new LlmQueryActivity(factory, options), factory);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_ReturnsContentAndTokenParams()
    {
        var client = new StubLlmClient(_ => Task.FromResult(
            new LlmResponse("hello world", "srv-model", PromptTokens: 11, CompletionTokens: 7, TotalTokens: 18, FinishReason: "stop")));
        var (activity, _) = Build(client);

        var result = await activity.ExecuteAsync(Ctx(), Cfg(new { prompt = "hi" }), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("hello world");
        result.OutputParameters["model"].Should().Be("srv-model");
        result.OutputParameters["promptTokens"].Should().Be("11");
        result.OutputParameters["completionTokens"].Should().Be("7");
        result.OutputParameters["totalTokens"].Should().Be("18");
        result.OutputParameters["finishReason"].Should().Be("stop");
    }

    [Fact]
    public async Task ExecuteAsync_MissingUsage_TokenParamsPresentButEmpty()
    {
        var client = new StubLlmClient(_ => Task.FromResult(new LlmResponse("answer", "m")));
        var (activity, _) = Build(client);

        var result = await activity.ExecuteAsync(Ctx(), Cfg(new { prompt = "hi" }), CancellationToken.None);

        result.Success.Should().BeTrue();
        // Contract: the token/finish-reason keys are ALWAYS present in OutputParameters, just
        // empty strings when the server didn't return usage/finish_reason data.
        result.OutputParameters.Should().ContainKeys("promptTokens", "completionTokens", "totalTokens", "finishReason");
        result.OutputParameters["promptTokens"].Should().Be("");
        result.OutputParameters["totalTokens"].Should().Be("");
        result.OutputParameters["finishReason"].Should().Be("");
    }

    [Fact]
    public async Task ExecuteAsync_Disabled_FailsWithClearMessage()
    {
        var client = new StubLlmClient(_ => throw new InvalidOperationException("should not be called"));
        var (activity, factory) = Build(client, enabled: false);

        var result = await activity.ExecuteAsync(Ctx(), Cfg(new { prompt = "hi" }), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("Llm:Enabled=false");
        factory.Created.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_MissingPrompt_Fails()
    {
        var (activity, _) = Build(new StubLlmClient(_ => Task.FromResult(new LlmResponse("x", "m"))));

        var result = await activity.ExecuteAsync(Ctx(), Cfg(new { model = "m" }), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("prompt");
    }

    [Fact]
    public async Task ExecuteAsync_CloudMetadataBaseUrl_RejectedBeforeCall()
    {
        var (activity, factory) = Build(new StubLlmClient(_ => Task.FromResult(new LlmResponse("x", "m"))));

        var result = await activity.ExecuteAsync(
            Ctx(), Cfg(new { prompt = "hi", baseUrl = "http://169.254.169.254/v1" }), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("cloud-metadata");
        factory.Created.Should().BeFalse();
    }

    [Theory]
    [InlineData(5.0)]
    [InlineData(-0.5)]
    public async Task ExecuteAsync_TemperatureOutOfRange_Fails(double temperature)
    {
        var (activity, _) = Build(new StubLlmClient(_ => Task.FromResult(new LlmResponse("x", "m"))));

        var result = await activity.ExecuteAsync(
            Ctx(), Cfg(new { prompt = "hi", temperature }), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("temperature");
    }

    [Fact]
    public async Task ExecuteAsync_NonPositiveMaxTokens_Fails()
    {
        var (activity, _) = Build(new StubLlmClient(_ => Task.FromResult(new LlmResponse("x", "m"))));

        var result = await activity.ExecuteAsync(
            Ctx(), Cfg(new { prompt = "hi", maxTokens = 0 }), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("maxTokens");
    }

    [Fact]
    public async Task ExecuteAsync_LlmException_MappedToFailure()
    {
        var client = new StubLlmClient(_ => Task.FromException<LlmResponse>(
            new LlmException(LlmErrorKind.Unauthorized, "bad key", httpStatus: 401)));
        var (activity, _) = Build(client);

        var result = await activity.ExecuteAsync(Ctx(), Cfg(new { prompt = "hi" }), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("Unauthorized");
        result.ErrorOutput.Should().Contain("HTTP 401");
    }

    [Fact]
    public async Task ExecuteAsync_PerNodeOverrides_PassedToFactory()
    {
        LlmRequest? seen = null;
        var client = new StubLlmClient(r => { seen = r; return Task.FromResult(new LlmResponse("x", "m")); });
        var (activity, factory) = Build(client);

        await activity.ExecuteAsync(Ctx(), Cfg(new
        {
            prompt = "hi",
            systemPrompt = "be brief",
            model = "llama3",
            baseUrl = "http://localhost:11434/v1",
            apiKey = "sk-node",
            maxTokens = 256,
            temperature = 0.4,
            timeoutSeconds = 30,
            jsonMode = true,
        }), CancellationToken.None);

        factory.LastOverrides!.Model.Should().Be("llama3");
        factory.LastOverrides.BaseUrl.Should().Be("http://localhost:11434/v1");
        factory.LastOverrides.ApiKey.Should().Be("sk-node");
        factory.LastOverrides.MaxTokens.Should().Be(256);
        factory.LastOverrides.Temperature.Should().Be(0.4);
        factory.LastOverrides.TimeoutSeconds.Should().Be(30);
        seen!.SystemPrompt.Should().Be("be brief");
        seen.JsonMode.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_NoOverrides_FactoryGetsNullsForGlobalFallback()
    {
        var (activity, factory) = Build(new StubLlmClient(_ => Task.FromResult(new LlmResponse("x", "m"))));

        await activity.ExecuteAsync(Ctx(), Cfg(new { prompt = "hi" }), CancellationToken.None);

        // Empty overrides → factory resolves everything from the global Llm:* config.
        factory.LastOverrides!.BaseUrl.Should().BeNull();
        factory.LastOverrides.ApiKey.Should().BeNull();
        factory.LastOverrides.Model.Should().BeNull();
        factory.LastOverrides.Temperature.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_DisabledGate_FlipsLiveAfterConfigReload()
    {
        // Hot-reload: LlmQueryActivity reads IOptionsMonitor<LlmOptions>.CurrentValue per execution,
        // so toggling Llm:Enabled in the Settings UI takes effect without a restart. Drive the
        // monitor (the test stand-in for a reloadOnChange config reload) from disabled→enabled
        // between two acts and assert the gate flips.
        var client = new StubLlmClient(_ => Task.FromResult(new LlmResponse("live", "m")));
        var factory = new StubFactory(client);
        var monitor = new MutableOptionsMonitor<LlmOptions>(new LlmOptions
        {
            Enabled = false,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o-mini",
        });
        var activity = new LlmQueryActivity(factory, monitor);

        // Disabled: gate rejects before the client is touched.
        var blocked = await activity.ExecuteAsync(Ctx(), Cfg(new { prompt = "hi" }), CancellationToken.None);
        blocked.Success.Should().BeFalse();
        blocked.ErrorOutput.Should().Contain("Llm:Enabled=false");

        // Simulate the operator enabling LLM in the Settings UI → config reload.
        monitor.Set(new LlmOptions
        {
            Enabled = true,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o-mini",
        });

        // Same activity instance, no re-creation → next execution succeeds.
        var allowed = await activity.ExecuteAsync(Ctx(), Cfg(new { prompt = "hi" }), CancellationToken.None);
        allowed.Success.Should().BeTrue();
        allowed.Output.Should().Be("live");
    }
}
