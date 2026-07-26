using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace NodePilot.Ai.Tests;

/// <summary>
/// Error classification and response parsing of <see cref="OpenAiCompatibleLlmClient"/>. Every
/// failure has to surface as an <see cref="LlmException"/> with the right
/// <see cref="LlmErrorKind"/> — the UI maps that kind onto the message an operator sees, so
/// "unreachable" and "unauthorized" must not collapse into a generic error.
/// <see cref="OpenAiCompatibleLlmClientTests"/> covers the happy paths and streaming.
/// </summary>
public sealed class OpenAiCompatibleLlmClientErrorTests : IDisposable
{
    private readonly WireMockServer _server;

    public OpenAiCompatibleLlmClientErrorTests() => _server = WireMockServer.Start();

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
    }

    [Fact]
    public async Task CompleteAsync_UnreachableEndpoint_ThrowsUnreachable()
    {
        // Port 1 on loopback: connection refused before any HTTP exchange happens.
        var client = Client(baseUrl: "http://127.0.0.1:1");

        var act = () => client.CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<LlmException>()).Which.Kind.Should().Be(LlmErrorKind.Unreachable);
    }

    [Fact]
    public async Task CompleteAsync_Forbidden_IsClassifiedAsUnauthorized()
    {
        Respond(403, "{\"error\":\"no access\"}");

        var act = () => Client().CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<LlmException>()).Which.Kind.Should().Be(LlmErrorKind.Unauthorized,
            "a 403 is a credential problem for the operator, same as a 401");
    }

    [Fact]
    public async Task CompleteAsync_ServerError_CarriesTheHttpStatus()
    {
        Respond(502, "upstream died");

        var act = () => Client().CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<LlmException>()).Which.HttpStatus.Should().Be(502);
    }

    [Fact]
    public async Task CompleteAsync_ErrorResponse_NamesTheHttpStatusInTheMessage()
    {
        Respond(400, "model 'test-model' does not exist");

        var act = () => Client().CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        // The upstream body is deliberately not spliced into the message — it can carry
        // provider internals. The status is what the operator acts on; the body goes to the log.
        (await act.Should().ThrowAsync<LlmException>())
            .Which.Message.Should().Contain("400");
    }

    [Fact]
    public async Task CompleteAsync_OversizedContentLength_IsRejectedWithoutReadingTheBody()
    {
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Length", "999999999")
                .WithBody("{}"));

        var act = () => Client().CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<LlmException>())
            .Which.Kind.Should().Be(LlmErrorKind.MalformedResponse);
    }

    [Fact]
    public async Task CompleteAsync_ResponseWithNeitherContentNorToolCalls_IsMalformed()
    {
        RespondJson(new
        {
            model = "test-model",
            choices = new[] { new { message = new { role = "assistant" } } },
        });

        var act = () => Client().CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<LlmException>())
            .Which.Kind.Should().Be(LlmErrorKind.MalformedResponse);
    }

    [Fact]
    public async Task CompleteAsync_ParsesTheUsageTokenCounts()
    {
        RespondJson(new
        {
            model = "test-model",
            choices = new[] { new { message = new { role = "assistant", content = "ok" } } },
            usage = new { prompt_tokens = 12, completion_tokens = 34, total_tokens = 46 },
        });

        var response = await Client().CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        response.PromptTokens.Should().Be(12);
        response.CompletionTokens.Should().Be(34);
        response.TotalTokens.Should().Be(46);
    }

    [Fact]
    public async Task CompleteAsync_PartialUsageBlock_LeavesTheMissingCountsNull()
    {
        RespondJson(new
        {
            model = "test-model",
            choices = new[] { new { message = new { role = "assistant", content = "ok" } } },
            usage = new { prompt_tokens = 7 },
        });

        var response = await Client().CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        response.PromptTokens.Should().Be(7);
        response.CompletionTokens.Should().BeNull();
        response.TotalTokens.Should().BeNull();
    }

    [Fact]
    public async Task CompleteAsync_WithoutAModelEcho_FallsBackToTheConfiguredModel()
    {
        RespondJson(new
        {
            choices = new[] { new { message = new { role = "assistant", content = "ok" } } },
        });

        var response = await Client().CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        response.Model.Should().Be("test-model");
    }

    [Fact]
    public async Task CompleteAsync_ModelEcho_IsPreferredOverTheConfiguredModel()
    {
        RespondJson(new
        {
            model = "gpt-4o-mini-2024",
            choices = new[] { new { message = new { role = "assistant", content = "ok" } } },
        });

        var response = await Client().CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        response.Model.Should().Be("gpt-4o-mini-2024",
            "the echo tells the operator which model actually served the request");
    }

    [Fact]
    public async Task CompleteAsync_FinishReason_IsSurfaced()
    {
        RespondJson(new
        {
            model = "test-model",
            choices = new[]
            {
                new { message = new { role = "assistant", content = "truncated" }, finish_reason = "length" },
            },
        });

        var response = await Client().CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        response.FinishReason.Should().Be("length",
            "a 'length' finish is how the caller learns the answer was cut off");
    }

    // ---------------------------------------------------------------- helpers

    private void Respond(int status, string body) =>
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

    private void RespondJson(object body) =>
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(body));

    private static LlmRequest Prompt() => new(
        SystemPrompt: "you are a test",
        UserPrompt: "hello",
        JsonMode: false);

    private OpenAiCompatibleLlmClient Client(string? baseUrl = null) => new(
        new SingleClientHttpClientFactory(),
        new LlmClientConfig(
            BaseUrl: (baseUrl ?? _server.Url!).TrimEnd('/'),
            ApiKey: null,
            Model: "test-model",
            MaxTokens: 100,
            Temperature: null,
            TimeoutSeconds: 30),
        NullLogger<OpenAiCompatibleLlmClient>.Instance);

    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
