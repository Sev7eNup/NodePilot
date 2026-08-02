using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace NodePilot.Ai.Tests;

/// <summary>
/// Wire-level behavior of <see cref="OpenAiResponsesLlmClient"/>: the Responses request shape
/// (<c>input</c>/<c>max_output_tokens</c>/<c>text.format</c>/flat tools), parsing of the
/// <c>output[]</c> envelope, the typed SSE event stream, and the deliberate absence of the
/// chat-completions compatibility fallbacks. Runs against a local WireMockServer.
/// </summary>
public sealed class OpenAiResponsesLlmClientTests : IDisposable
{
    private const string Path = "/v1/responses";

    private readonly WireMockServer _server;

    public OpenAiResponsesLlmClientTests()
    {
        _server = WireMockServer.Start();
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
    }

    private OpenAiResponsesLlmClient BuildClient(int? timeoutSeconds = null)
    {
        var config = new LlmClientConfig(
            Endpoint: LlmEndpointGuard.ResolveEndpoint(_server.Url!.TrimEnd('/') + Path),
            ApiKey: null,
            Model: "test-model",
            MaxTokens: 100,
            Temperature: null,
            TimeoutSeconds: timeoutSeconds ?? 90);
        return new OpenAiResponsesLlmClient(
            new SingleClientHttpClientFactory(), config, NullLogger<OpenAiResponsesLlmClient>.Instance);
    }

    private void Respond(object body, int status = 200) =>
        _server.Given(Request.Create().WithPath(Path).UsingPost())
               .RespondWith(Response.Create().WithStatusCode(status).WithBodyAsJson(body));

    private void RespondRaw(string body, int status = 200) =>
        _server.Given(Request.Create().WithPath(Path).UsingPost())
               .RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

    private void RespondSse(string sse) =>
        _server.Given(Request.Create().WithPath(Path).UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "text/event-stream").WithBody(sse));

    private JsonElement LastRequest() =>
        JsonDocument.Parse(_server.LogEntries.Last().RequestMessage!.Body!).RootElement.Clone();

    private static object TextResponse(string text, string status = "completed") => new
    {
        model = "test-model",
        status,
        output = new object[]
        {
            new { type = "message", role = "assistant", content = new object[] { new { type = "output_text", text } } },
        },
    };

    private static LlmRequest Prompt(bool jsonMode = false) => new("sys", "user", JsonMode: jsonMode);

    // ---- Non-streaming --------------------------------------------------------------

    [Fact]
    public async Task CompleteAsync_HappyPath_ReturnsTextFromOutputMessage()
    {
        Respond(TextResponse("hello world"));

        var resp = await BuildClient().CompleteAsync(Prompt(), CancellationToken.None);

        resp.Content.Should().Be("hello world");
        resp.Model.Should().Be("test-model");
        resp.FinishReason.Should().Be("stop");
    }

    [Fact]
    public async Task CompleteAsync_PostsToTheConfiguredUrlVerbatim()
    {
        // The /responses path is the endpoint itself — nothing may be appended to it.
        Respond(TextResponse("ok"));

        await BuildClient().CompleteAsync(Prompt(), CancellationToken.None);

        _server.LogEntries.Should().ContainSingle()
            .Which.RequestMessage!.Path.Should().Be(Path);
    }

    [Fact]
    public async Task CompleteAsync_WithApiKey_SendsItAsABearerToken()
    {
        // Auth lives in the shared LlmHttpTransport, so this covers both dialects.
        Respond(TextResponse("ok"));
        var config = new LlmClientConfig(
            Endpoint: LlmEndpointGuard.ResolveEndpoint(_server.Url!.TrimEnd('/') + Path),
            ApiKey: "sk-secret", Model: "test-model", MaxTokens: 100, Temperature: null, TimeoutSeconds: 90);
        var client = new OpenAiResponsesLlmClient(
            new SingleClientHttpClientFactory(), config, NullLogger<OpenAiResponsesLlmClient>.Instance);

        await client.CompleteAsync(Prompt(), CancellationToken.None);

        _server.LogEntries.Single().RequestMessage!.Headers!["Authorization"]
            .Should().ContainSingle().Which.Should().Be("Bearer sk-secret");
    }

    [Fact]
    public async Task CompleteAsync_WithoutApiKey_SendsNoAuthorizationHeader()
    {
        Respond(TextResponse("ok"));

        await BuildClient().CompleteAsync(Prompt(), CancellationToken.None);

        _server.LogEntries.Single().RequestMessage!.Headers!.ContainsKey("Authorization").Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_SendsTemperatureOnlyWhenSet()
    {
        Respond(TextResponse("ok"));
        var config = new LlmClientConfig(
            Endpoint: LlmEndpointGuard.ResolveEndpoint(_server.Url!.TrimEnd('/') + Path),
            ApiKey: null, Model: "test-model", MaxTokens: 100, Temperature: 0.25, TimeoutSeconds: 90);
        var client = new OpenAiResponsesLlmClient(
            new SingleClientHttpClientFactory(), config, NullLogger<OpenAiResponsesLlmClient>.Instance);

        await client.CompleteAsync(Prompt(), CancellationToken.None);

        LastRequest().GetProperty("temperature").GetDouble().Should().Be(0.25);
    }

    [Fact]
    public async Task CompleteAsync_SendsInputAndMaxOutputTokens_NotMessagesOrMaxTokens()
    {
        Respond(TextResponse("ok"));

        await BuildClient().CompleteAsync(Prompt(), CancellationToken.None);

        var body = LastRequest();
        body.TryGetProperty("messages", out _).Should().BeFalse();
        body.TryGetProperty("max_tokens", out _).Should().BeFalse();
        body.GetProperty("max_output_tokens").GetInt32().Should().Be(100);

        var input = body.GetProperty("input");
        input.GetArrayLength().Should().Be(2);
        input[0].GetProperty("role").GetString().Should().Be("system");
        input[0].GetProperty("content").GetString().Should().Be("sys");
        input[1].GetProperty("role").GetString().Should().Be("user");
        input[1].GetProperty("content").GetString().Should().Be("user");
    }

    [Fact]
    public async Task CompleteAsync_SendsStoreFalse()
    {
        // The API defaults to store: true, which would park every prompt in the OpenAI dashboard.
        Respond(TextResponse("ok"));

        await BuildClient().CompleteAsync(Prompt(), CancellationToken.None);

        LastRequest().GetProperty("store").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_JsonMode_SendsTextFormatJsonObject()
    {
        Respond(TextResponse("{}"));

        await BuildClient().CompleteAsync(Prompt(jsonMode: true), CancellationToken.None);

        var body = LastRequest();
        body.TryGetProperty("response_format", out _).Should().BeFalse();
        body.GetProperty("text").GetProperty("format").GetProperty("type").GetString().Should().Be("json_object");
    }

    [Fact]
    public async Task CompleteAsync_NoJsonMode_OmitsTextFormat()
    {
        Respond(TextResponse("ok"));

        await BuildClient().CompleteAsync(Prompt(), CancellationToken.None);

        LastRequest().TryGetProperty("text", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_WithTools_SendsFlatFunctionSchemaWithoutNestedFunctionObject()
    {
        Respond(TextResponse("ok"));
        var request = Prompt() with { Tools = new[] { Tool("analyze", strict: true) } };

        await BuildClient().CompleteAsync(request, CancellationToken.None);

        var tool = LastRequest().GetProperty("tools")[0];
        tool.TryGetProperty("function", out _).Should().BeFalse();
        tool.GetProperty("type").GetString().Should().Be("function");
        tool.GetProperty("name").GetString().Should().Be("analyze");
        tool.GetProperty("strict").GetBoolean().Should().BeTrue();
        tool.GetProperty("parameters").GetProperty("type").GetString().Should().Be("object");
        LastRequest().GetProperty("tool_choice").GetString().Should().Be("auto");
    }

    [Fact]
    public async Task CompleteAsync_FunctionCallOutput_ParsesCallIdNameAndArguments()
    {
        Respond(new
        {
            model = "test-model",
            status = "completed",
            output = new object[]
            {
                new { type = "reasoning", summary = Array.Empty<object>() },
                new { type = "function_call", call_id = "call_1", name = "analyze", arguments = "{\"id\":7}" },
            },
        });

        var resp = await BuildClient().CompleteAsync(Prompt(), CancellationToken.None);

        resp.ToolCalls.Should().ContainSingle();
        resp.ToolCalls![0].Id.Should().Be("call_1");
        resp.ToolCalls[0].Name.Should().Be("analyze");
        resp.ToolCalls[0].ArgumentsJson.Should().Be("{\"id\":7}");
        resp.FinishReason.Should().Be("tool_calls");
    }

    [Fact]
    public async Task CompleteAsync_SerializesAssistantToolCallAndToolResultAsFunctionCallItems()
    {
        // The one structural difference to chat completions: an assistant turn with tool calls is
        // not one message but a (optional) message plus one function_call item per call.
        Respond(TextResponse("done"));
        var request = Prompt() with
        {
            Conversation = new[]
            {
                new LlmMessage("user", "go"),
                new LlmMessage("assistant", "checking", ToolCalls: new[] { new LlmToolCall("call_1", "analyze", "{}") }),
                new LlmMessage("tool", "result-json", ToolCallId: "call_1"),
            },
        };

        await BuildClient().CompleteAsync(request, CancellationToken.None);

        var input = LastRequest().GetProperty("input");
        input.GetArrayLength().Should().Be(5); // system, user, assistant text, function_call, function_call_output
        input[2].GetProperty("content").GetString().Should().Be("checking");
        input[3].GetProperty("type").GetString().Should().Be("function_call");
        input[3].GetProperty("call_id").GetString().Should().Be("call_1");
        input[3].GetProperty("name").GetString().Should().Be("analyze");
        input[4].GetProperty("type").GetString().Should().Be("function_call_output");
        input[4].GetProperty("call_id").GetString().Should().Be("call_1");
        input[4].GetProperty("output").GetString().Should().Be("result-json");
    }

    [Fact]
    public async Task CompleteAsync_AssistantToolCallWithoutText_OmitsTheEmptyMessageItem()
    {
        Respond(TextResponse("done"));
        var request = Prompt() with
        {
            Conversation = new[]
            {
                new LlmMessage("assistant", "", ToolCalls: new[] { new LlmToolCall("call_1", "analyze", "{}") }),
            },
        };

        await BuildClient().CompleteAsync(request, CancellationToken.None);

        var input = LastRequest().GetProperty("input");
        input.GetArrayLength().Should().Be(2); // system + function_call only
        input[1].GetProperty("type").GetString().Should().Be("function_call");
    }

    [Fact]
    public async Task CompleteAsync_ParsesInputAndOutputTokenUsage()
    {
        Respond(new
        {
            model = "test-model",
            status = "completed",
            output = new object[]
            {
                new { type = "message", role = "assistant", content = new object[] { new { type = "output_text", text = "hi" } } },
            },
            usage = new { input_tokens = 11, output_tokens = 4, total_tokens = 15 },
        });

        var resp = await BuildClient().CompleteAsync(Prompt(), CancellationToken.None);

        resp.PromptTokens.Should().Be(11);
        resp.CompletionTokens.Should().Be(4);
        resp.TotalTokens.Should().Be(15);
    }

    [Fact]
    public async Task CompleteAsync_IncompleteMaxOutputTokens_ReportsLengthFinishReason()
    {
        Respond(new
        {
            model = "test-model",
            status = "incomplete",
            incomplete_details = new { reason = "max_output_tokens" },
            output = new object[]
            {
                new { type = "message", role = "assistant", content = new object[] { new { type = "output_text", text = "trunc" } } },
            },
        });

        var resp = await BuildClient().CompleteAsync(Prompt(), CancellationToken.None);

        resp.Content.Should().Be("trunc");
        resp.FinishReason.Should().Be("length");
    }

    [Fact]
    public async Task CompleteAsync_StatusFailed_ThrowsUpstreamError()
    {
        // A failed run comes back as HTTP 200 — it must not read as an empty answer.
        Respond(new
        {
            model = "test-model",
            status = "failed",
            error = new { message = "the model melted" },
            output = Array.Empty<object>(),
        });

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            BuildClient().CompleteAsync(Prompt(), CancellationToken.None));

        ex.Kind.Should().Be(LlmErrorKind.UpstreamError);
        ex.BodyExcerpt.Should().Contain("the model melted");
    }

    [Fact]
    public async Task CompleteAsync_MissingOutputArray_ThrowsMalformedResponse()
    {
        Respond(new { model = "test-model", status = "completed" });

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            BuildClient().CompleteAsync(Prompt(), CancellationToken.None));

        ex.Kind.Should().Be(LlmErrorKind.MalformedResponse);
        ex.Message.Should().Contain("'output'");
    }

    [Fact]
    public async Task CompleteAsync_OutputWithNeitherTextNorFunctionCall_ThrowsMalformedResponse()
    {
        Respond(new
        {
            model = "test-model",
            status = "completed",
            output = new object[] { new { type = "reasoning", summary = Array.Empty<object>() } },
        });

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            BuildClient().CompleteAsync(Prompt(), CancellationToken.None));

        ex.Kind.Should().Be(LlmErrorKind.MalformedResponse);
    }

    [Fact]
    public async Task CompleteAsync_NotJson_ThrowsMalformedResponse()
    {
        RespondRaw("<html>gateway</html>");

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            BuildClient().CompleteAsync(Prompt(), CancellationToken.None));

        ex.Kind.Should().Be(LlmErrorKind.MalformedResponse);
    }

    [Theory]
    [InlineData(401, LlmErrorKind.Unauthorized)]
    [InlineData(403, LlmErrorKind.Unauthorized)]
    [InlineData(429, LlmErrorKind.RateLimited)]
    [InlineData(500, LlmErrorKind.UpstreamError)]
    public async Task CompleteAsync_ErrorStatus_MapsToKind(int status, LlmErrorKind expected)
    {
        RespondRaw("upstream said no", status);

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            BuildClient().CompleteAsync(Prompt(), CancellationToken.None));

        ex.Kind.Should().Be(expected);
        ex.HttpStatus.Should().Be(status);
        ex.BodyExcerpt.Should().Contain("upstream said no");
    }

    [Fact]
    public async Task CompleteAsync_SlowerThanTimeout_ThrowsTimeout()
    {
        _server.Given(Request.Create().WithPath(Path).UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithDelay(TimeSpan.FromSeconds(3)).WithBodyAsJson(TextResponse("late")));

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            BuildClient(timeoutSeconds: 1).CompleteAsync(Prompt(), CancellationToken.None));

        ex.Kind.Should().Be(LlmErrorKind.Timeout);
    }

    // ---- No chat-completions fallbacks ----------------------------------------------
    // The four quirk retries of OpenAiCompatibleLlmClient are Chat-Completions-only. Firing them
    // here would send a second pointless request and could silently drop text.format / strict.

    [Theory]
    [InlineData("{\"error\":{\"message\":\"Use 'max_completion_tokens' instead.\"}}")]
    [InlineData("{\"error\":{\"message\":\"unsupported response format\"}}")]
    [InlineData("{\"error\":{\"message\":\"strict function schemas are unsupported\"}}")]
    public async Task CompleteAsync_BadRequest_DoesNotRetry(string errorBody)
    {
        RespondRaw(errorBody, 400);
        var request = Prompt(jsonMode: true) with { Tools = new[] { Tool("analyze", strict: true) } };

        await Assert.ThrowsAsync<LlmException>(() =>
            BuildClient().CompleteAsync(request, CancellationToken.None));

        _server.LogEntries.Should().ContainSingle();
    }

    // ---- Streaming (SSE) ------------------------------------------------------------

    private static string Sse(params string[] events) =>
        string.Concat(events.Select(e => $"data: {e}\n\n"));

    private const string TextStream =
        """{"type":"response.output_text.delta","delta":"Hello"}""";

    private static async Task<(List<string> deltas, LlmStreamEvent? done)> Collect(
        OpenAiResponsesLlmClient client, LlmRequest request)
    {
        var deltas = new List<string>();
        LlmStreamEvent? done = null;
        await foreach (var e in client.StreamAsync(request, CancellationToken.None))
        {
            if (e.Done) done = e;
            else if (e.ContentDelta is { } d) deltas.Add(d);
        }
        return (deltas, done);
    }

    [Fact]
    public async Task StreamAsync_HappyPath_YieldsTextDeltasThenDoneWithUsage()
    {
        RespondSse(Sse(
            """{"type":"response.created","response":{"status":"in_progress"}}""",
            TextStream,
            """{"type":"response.output_text.delta","delta":" world"}""",
            """{"type":"response.completed","response":{"model":"test-model","status":"completed","usage":{"input_tokens":9,"output_tokens":2}}}"""));

        var (deltas, done) = await Collect(BuildClient(), Prompt());

        string.Join("", deltas).Should().Be("Hello world");
        done.Should().NotBeNull();
        done!.Model.Should().Be("test-model");
        done.PromptTokens.Should().Be(9);
        done.CompletionTokens.Should().Be(2);
        done.FinishReason.Should().Be("stop");
        done.GenerationMs.Should().NotBeNull();
    }

    [Fact]
    public async Task StreamAsync_SendsStreamTrueAndNeverStreamOptions()
    {
        RespondSse(Sse(TextStream));

        await Collect(BuildClient(), Prompt());

        var body = LastRequest();
        body.GetProperty("stream").GetBoolean().Should().BeTrue();
        body.TryGetProperty("stream_options", out _).Should().BeFalse();
    }

    [Fact]
    public async Task StreamAsync_AccumulatesFunctionCallArgumentsAcrossDeltas()
    {
        RespondSse(Sse(
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","call_id":"call_1","name":"analyze"}}""",
            """{"type":"response.function_call_arguments.delta","output_index":0,"delta":"{\"id\":"}""",
            """{"type":"response.function_call_arguments.delta","output_index":0,"delta":"7}"}""",
            """{"type":"response.completed","response":{"model":"test-model","status":"completed"}}"""));

        var (_, done) = await Collect(BuildClient(), Prompt());

        done!.ToolCalls.Should().ContainSingle();
        done.ToolCalls![0].Id.Should().Be("call_1");
        done.ToolCalls[0].Name.Should().Be("analyze");
        done.ToolCalls[0].ArgumentsJson.Should().Be("{\"id\":7}");
        done.FinishReason.Should().Be("tool_calls");
    }

    [Fact]
    public async Task StreamAsync_TwoParallelFunctionCalls_KeyedByOutputIndex_BothSurface()
    {
        RespondSse(Sse(
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","call_id":"call_1","name":"first"}}""",
            """{"type":"response.output_item.added","output_index":1,"item":{"type":"function_call","call_id":"call_2","name":"second"}}""",
            """{"type":"response.function_call_arguments.delta","output_index":1,"delta":"{\"b\":2}"}""",
            """{"type":"response.function_call_arguments.delta","output_index":0,"delta":"{\"a\":1}"}""",
            """{"type":"response.completed","response":{"model":"test-model","status":"completed"}}"""));

        var (_, done) = await Collect(BuildClient(), Prompt());

        done!.ToolCalls.Should().HaveCount(2);
        done.ToolCalls![0].Name.Should().Be("first");
        done.ToolCalls[0].ArgumentsJson.Should().Be("{\"a\":1}");
        done.ToolCalls[1].Name.Should().Be("second");
        done.ToolCalls[1].ArgumentsJson.Should().Be("{\"b\":2}");
    }

    [Fact]
    public async Task StreamAsync_OutputItemDoneWithoutArgumentDeltas_StillYieldsTheCall()
    {
        RespondSse(Sse(
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","call_id":"call_1","name":"analyze","arguments":"{\"id\":7}"}}""",
            """{"type":"response.completed","response":{"model":"test-model","status":"completed"}}"""));

        var (_, done) = await Collect(BuildClient(), Prompt());

        done!.ToolCalls.Should().ContainSingle();
        done.ToolCalls![0].ArgumentsJson.Should().Be("{\"id\":7}");
    }

    [Fact]
    public async Task StreamAsync_IgnoresReasoningAndUnknownEventTypes()
    {
        RespondSse(Sse(
            """{"type":"response.reasoning_summary_text.delta","delta":"thinking hard"}""",
            """{"type":"response.some.future.event","delta":"noise"}""",
            "not-json-at-all",
            TextStream,
            """{"type":"response.completed","response":{"model":"test-model","status":"completed"}}"""));

        var (deltas, done) = await Collect(BuildClient(), Prompt());

        string.Join("", deltas).Should().Be("Hello");
        done!.ToolCalls.Should().BeNull();
    }

    [Fact]
    public async Task StreamAsync_ResponseFailedEvent_ThrowsUpstreamError()
    {
        RespondSse(Sse(
            TextStream,
            """{"type":"response.failed","response":{"status":"failed","error":{"message":"model unavailable"}}}"""));

        var ex = await Assert.ThrowsAsync<LlmException>(() => Collect(BuildClient(), Prompt()));

        ex.Kind.Should().Be(LlmErrorKind.UpstreamError);
        ex.BodyExcerpt.Should().Contain("model unavailable");
    }

    [Fact]
    public async Task StreamAsync_IncompleteEvent_ReportsLengthFinishReason()
    {
        RespondSse(Sse(
            TextStream,
            """{"type":"response.incomplete","response":{"model":"test-model","status":"incomplete","incomplete_details":{"reason":"max_output_tokens"}}}"""));

        var (_, done) = await Collect(BuildClient(), Prompt());

        done!.FinishReason.Should().Be("length");
    }

    [Fact]
    public async Task StreamAsync_ToolCallOnlyRound_StillReportsGenerationMs()
    {
        // No text ever streams in a tool round — the generation clock must still start.
        RespondSse(Sse(
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","call_id":"call_1","name":"analyze"}}""",
            """{"type":"response.function_call_arguments.delta","output_index":0,"delta":"{}"}""",
            """{"type":"response.completed","response":{"model":"test-model","status":"completed"}}"""));

        var (deltas, done) = await Collect(BuildClient(), Prompt());

        deltas.Should().BeEmpty();
        done!.GenerationMs.Should().NotBeNull();
    }

    [Fact]
    public async Task StreamAsync_NoOutputEmitted_LeavesGenerationMsNull()
    {
        RespondSse(Sse("""{"type":"response.completed","response":{"model":"test-model","status":"completed"}}"""));

        var (deltas, done) = await Collect(BuildClient(), Prompt());

        deltas.Should().BeEmpty();
        done!.GenerationMs.Should().BeNull();
    }

    [Fact]
    public async Task StreamAsync_Non200_ThrowsClassifiedException()
    {
        RespondRaw("nope", 401);

        var ex = await Assert.ThrowsAsync<LlmException>(() => Collect(BuildClient(), Prompt()));

        ex.Kind.Should().Be(LlmErrorKind.Unauthorized);
    }

    private static LlmToolDefinition Tool(string name, bool strict) => new(
        name,
        $"does {name}",
        JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone(),
        Strict: strict);

    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
