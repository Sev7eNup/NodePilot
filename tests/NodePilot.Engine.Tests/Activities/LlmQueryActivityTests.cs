using System.Text.Json;
using FluentAssertions;
using NodePilot.Ai;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Activities;
using Xunit;
using NodePilot.TestCommons;

namespace NodePilot.Engine.Tests.Activities;

public sealed class LlmQueryActivityTests
{
    // Test doubles come from TestCommons (FakeLlmClient / FakeLlmClientFactory) — this file
    // previously carried private near-copies of both while already importing TestCommons
    // (coherence audit 2026-08, consolidation residue).

    private static StepExecutionContext Ctx() => new() { WorkflowExecutionId = Guid.NewGuid(), StepId = "step-1" };

    private static JsonElement Cfg(object o) => JsonSerializer.SerializeToElement(o);

    private static (LlmQueryActivity activity, FakeLlmClientFactory factory) Build(
        FakeLlmClient client, bool enabled = true)
    {
        var factory = new FakeLlmClientFactory(client);
        var options = new StaticOptionsMonitor<LlmOptions>(LlmTestOptions.WithProfile(enabled: enabled));
        return (new LlmQueryActivity(factory, options), factory);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_ReturnsContentAndTokenParams()
    {
        var client = new FakeLlmClient().EnqueueResponse(
            new LlmResponse("hello world", "srv-model", PromptTokens: 11, CompletionTokens: 7, TotalTokens: 18, FinishReason: "stop"));
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
        var client = new FakeLlmClient().EnqueueContent("answer", "m");
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
        // Nothing enqueued — the fake throws if the gate ever lets a call through.
        var client = new FakeLlmClient();
        var (activity, factory) = Build(client, enabled: false);

        var result = await activity.ExecuteAsync(Ctx(), Cfg(new { prompt = "hi" }), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("Llm:Enabled=false");
        factory.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_MissingPrompt_Fails()
    {
        var (activity, _) = Build(new FakeLlmClient());

        var result = await activity.ExecuteAsync(Ctx(), Cfg(new { model = "m" }), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("prompt");
    }

    [Fact]
    public async Task ExecuteAsync_CloudMetadataBaseUrl_RejectedBeforeCall()
    {
        var (activity, factory) = Build(new FakeLlmClient());

        var result = await activity.ExecuteAsync(
            Ctx(), Cfg(new { prompt = "hi", baseUrl = "http://169.254.169.254/v1" }), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("cloud-metadata");
        factory.Connections.Should().BeEmpty();
    }

    [Theory]
    [InlineData(5.0)]
    [InlineData(-0.5)]
    public async Task ExecuteAsync_TemperatureOutOfRange_Fails(double temperature)
    {
        var (activity, _) = Build(new FakeLlmClient());

        var result = await activity.ExecuteAsync(
            Ctx(), Cfg(new { prompt = "hi", temperature }), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("temperature");
    }

    [Fact]
    public async Task ExecuteAsync_NonPositiveMaxTokens_Fails()
    {
        var (activity, _) = Build(new FakeLlmClient());

        var result = await activity.ExecuteAsync(
            Ctx(), Cfg(new { prompt = "hi", maxTokens = 0 }), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("maxTokens");
    }

    [Fact]
    public async Task ExecuteAsync_LlmException_MappedToFailure()
    {
        var client = new FakeLlmClient().EnqueueException(
            new LlmException(LlmErrorKind.Unauthorized, "bad key", httpStatus: 401));
        var (activity, _) = Build(client);

        var result = await activity.ExecuteAsync(Ctx(), Cfg(new { prompt = "hi" }), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("Unauthorized");
        result.ErrorOutput.Should().Contain("HTTP 401");
    }

    [Fact]
    public async Task ExecuteAsync_PerNodeOverrides_PassedToFactory()
    {
        var client = new FakeLlmClient().EnqueueContent("x", "m");
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

        var overrides = factory.Connections.Should().ContainSingle().Subject!;
        overrides.Model.Should().Be("llama3");
        overrides.BaseUrl.Should().Be("http://localhost:11434/v1");
        overrides.ApiKey.Should().Be("sk-node");
        overrides.MaxTokens.Should().Be(256);
        overrides.Temperature.Should().Be(0.4);
        overrides.TimeoutSeconds.Should().Be(30);
        var seen = client.Calls.Should().ContainSingle().Subject;
        seen.SystemPrompt.Should().Be("be brief");
        seen.JsonMode.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_NoOverrides_FactoryGetsNullsForGlobalFallback()
    {
        var (activity, factory) = Build(new FakeLlmClient().EnqueueContent("x", "m"));

        await activity.ExecuteAsync(Ctx(), Cfg(new { prompt = "hi" }), CancellationToken.None);

        // Empty overrides → factory resolves everything from the global Llm:* config.
        var overrides = factory.Connections.Should().ContainSingle().Subject!;
        overrides.BaseUrl.Should().BeNull();
        overrides.ApiKey.Should().BeNull();
        overrides.Model.Should().BeNull();
        overrides.Temperature.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_DisabledGate_FlipsLiveAfterConfigReload()
    {
        // Hot-reload: LlmQueryActivity reads IOptionsMonitor<LlmOptions>.CurrentValue per execution,
        // so toggling Llm:Enabled in the Settings UI takes effect without a restart. Drive the
        // monitor (the test stand-in for a reloadOnChange config reload) from disabled→enabled
        // between two acts and assert the gate flips.
        var client = new FakeLlmClient().EnqueueContent("live", "m");
        var factory = new FakeLlmClientFactory(client);
        var monitor = new MutableOptionsMonitor<LlmOptions>(LlmTestOptions.WithProfile(enabled: false));
        var activity = new LlmQueryActivity(factory, monitor);

        // Disabled: gate rejects before the client is touched.
        var blocked = await activity.ExecuteAsync(Ctx(), Cfg(new { prompt = "hi" }), CancellationToken.None);
        blocked.Success.Should().BeFalse();
        blocked.ErrorOutput.Should().Contain("Llm:Enabled=false");

        // Simulate the operator enabling LLM in the Settings UI → config reload.
        monitor.Set(LlmTestOptions.WithProfile(enabled: true));

        // Same activity instance, no re-creation → next execution succeeds.
        var allowed = await activity.ExecuteAsync(Ctx(), Cfg(new { prompt = "hi" }), CancellationToken.None);
        allowed.Success.Should().BeTrue();
        allowed.Output.Should().Be("live");
    }

    [Fact]
    public async Task ExecuteAsync_NoActiveProfile_FailsWithActionableMessage()
    {
        // The activity layers its per-node overrides on top of the ACTIVE profile, so without one
        // there is nothing to layer onto. The factory throws, the activity turns it into a step
        // failure rather than an unhandled exception.
        var factory = new NodePilot.Ai.LlmClientFactory(
            new StubHttpClientFactory(),
            new StaticOptionsMonitor<LlmOptions>(LlmTestOptions.EnabledWithoutProfile()),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var activity = new LlmQueryActivity(factory, new StaticOptionsMonitor<LlmOptions>(LlmTestOptions.EnabledWithoutProfile()));

        var result = await activity.ExecuteAsync(Ctx(), Cfg(new { prompt = "hi" }), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("No active LLM profile");
    }
}
