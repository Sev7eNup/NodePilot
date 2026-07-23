using NodePilot.Ai;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace NodePilot.Ai.Tests;

/// <summary>
/// Wire-level behavior of <see cref="OpenAiCompatibleLlmClient"/>: error classification per
/// HTTP status, timeout mapping, and detection of malformed responses. Uses its own
/// IHttpClientFactory stub so the test runs against a local WireMockServer instead of a real
/// LLM endpoint.
/// </summary>
public sealed class OpenAiCompatibleLlmClientTests : IDisposable
{
    private readonly WireMockServer _server;

    public OpenAiCompatibleLlmClientTests()
    {
        _server = WireMockServer.Start();
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
    }

    private OpenAiCompatibleLlmClient BuildClient(int? timeoutSeconds = null)
    {
        var config = new LlmClientConfig(
            BaseUrl: _server.Url!.TrimEnd('/'),
            ApiKey: null,
            Model: "test-model",
            MaxTokens: 100,
            Temperature: null,
            TimeoutSeconds: timeoutSeconds ?? 90);
        var factory = new SingleClientHttpClientFactory();
        return new OpenAiCompatibleLlmClient(factory, config, NullLogger<OpenAiCompatibleLlmClient>.Instance);
    }

    [Fact]
    public async Task CompleteAsync_HappyPath_ReturnsContent()
    {
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   model = "test-model",
                   choices = new[] { new { message = new { role = "assistant", content = "hello world" } } },
               }));

        var client = BuildClient();
        var resp = await client.CompleteAsync(new LlmRequest("sys", "user"), CancellationToken.None);

        resp.Content.Should().Be("hello world");
        resp.Model.Should().Be("test-model");
    }

    [Fact]
    public async Task CompleteAsync_Unauthorized_ThrowsWithUnauthorizedKind()
    {
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(401).WithBody("invalid api key"));

        var client = BuildClient();
        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            client.CompleteAsync(new LlmRequest("sys", "user"), CancellationToken.None));

        ex.Kind.Should().Be(LlmErrorKind.Unauthorized);
        ex.HttpStatus.Should().Be(401);
    }

    [Fact]
    public async Task CompleteAsync_RateLimited_ThrowsWithRateLimitedKind()
    {
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(429));

        var client = BuildClient();
        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            client.CompleteAsync(new LlmRequest("sys", "user"), CancellationToken.None));

        ex.Kind.Should().Be(LlmErrorKind.RateLimited);
    }

    [Fact]
    public async Task CompleteAsync_MalformedBody_ThrowsMalformedResponse()
    {
        // Server returns 200, but the body shape is wrong — no 'choices' array.
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   model = "test-model",
                   error = "something went sideways",
               }));

        var client = BuildClient();
        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            client.CompleteAsync(new LlmRequest("sys", "user"), CancellationToken.None));

        ex.Kind.Should().Be(LlmErrorKind.MalformedResponse);
    }

    [Fact]
    public async Task CompleteAsync_JsonMode400_RetriesWithoutResponseFormat()
    {
        // First call: body includes "response_format" → server responds 400 (typical LM Studio
        // behavior for models without JSON-mode support).
        // Second call: body WITHOUT "response_format" → server responds 200 with valid content.
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost()
                .WithBody(b => b != null && b.Contains("response_format")))
               .RespondWith(Response.Create().WithStatusCode(400)
                   .WithBody("{\"error\":\"model does not support response_format\"}"));
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost()
                .WithBody(b => b != null && !b.Contains("response_format")))
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   model = "test-model",
                   choices = new[] { new { message = new { content = "{\"name\":\"ok\"}" } } },
               }));

        var client = BuildClient();
        var resp = await client.CompleteAsync(
            new LlmRequest("sys", "user", JsonMode: true), CancellationToken.None);

        resp.Content.Should().Be("{\"name\":\"ok\"}");
    }

    [Fact]
    public async Task CompleteAsync_NonJsonMode400_DoesNotRetry_SurfacesUpstreamErrorWithBodyExcerpt()
    {
        // If the request wasn't JSON-mode at all, a 400 is a genuine upstream error —
        // no retry, just surface it directly with a BodyExcerpt for diagnostics.
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(400)
                   .WithBody("context_length_exceeded: requested 22000 > 8192"));

        var client = BuildClient();
        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            client.CompleteAsync(new LlmRequest("sys", "user"), CancellationToken.None));

        ex.Kind.Should().Be(LlmErrorKind.UpstreamError);
        ex.HttpStatus.Should().Be(400);
        ex.BodyExcerpt.Should().Contain("context_length_exceeded");
    }

    [Fact]
    public async Task CompleteAsync_JsonMode400_FallbackAlsoFails_SurfacesSecondError()
    {
        // If even the retry without response_format responds with 400 (a genuine context
        // overflow, model issue, etc.), we surface the SECOND error — that's the real cause.
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(400)
                   .WithBody("context length exceeded"));

        var client = BuildClient();
        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            client.CompleteAsync(new LlmRequest("sys", "user", JsonMode: true), CancellationToken.None));

        ex.Kind.Should().Be(LlmErrorKind.UpstreamError);
        ex.BodyExcerpt.Should().Contain("context length exceeded");
    }

    [Fact]
    public async Task CompleteAsync_MaxTokensUnsupported400_RetriesWithMaxCompletionTokens()
    {
        // Newer OpenAI models (o-series / GPT-5 era) reject `max_tokens` with a 400 and require
        // `max_completion_tokens` instead. First call (body has max_tokens) → 400 with the
        // typical unsupported_parameter body; retry (body has max_completion_tokens) → 200.
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost()
                .WithBody(b => b != null && b.Contains("max_tokens") && !b.Contains("max_completion_tokens")))
               .RespondWith(Response.Create().WithStatusCode(400)
                   .WithBody("{\"error\":{\"message\":\"Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead.\",\"type\":\"invalid_request_error\",\"param\":\"max_tokens\",\"code\":\"unsupported_parameter\"}}"));
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost()
                .WithBody(b => b != null && b.Contains("max_completion_tokens")))
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   model = "test-model",
                   choices = new[] { new { message = new { content = "recovered" } } },
               }));

        var client = BuildClient();
        var resp = await client.CompleteAsync(new LlmRequest("sys", "user"), CancellationToken.None);

        resp.Content.Should().Be("recovered");
        _server.LogEntries.Should().HaveCount(2);
        var retryBody = _server.LogEntries.Last().RequestMessage.Body!;
        retryBody.Should().Contain("max_completion_tokens");
        retryBody.Should().NotContain("\"max_tokens\"");

        // The compatibility decision is shared across short-lived clients for the same endpoint/model,
        // so subsequent calls do not pay another guaranteed HTTP 400 round-trip.
        var second = await BuildClient().CompleteAsync(
            new LlmRequest("sys", "user"), CancellationToken.None);
        second.Content.Should().Be("recovered");
        _server.LogEntries.Should().HaveCount(3);
        _server.LogEntries.Last().RequestMessage.Body.Should().Contain("max_completion_tokens");
    }

    [Fact]
    public async Task CompleteAsync_StrictToolsUnsupported_RetriesWithoutStrict()
    {
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost()
                .WithBody(b => b != null && b.Contains("\"strict\":true")))
            .RespondWith(Response.Create().WithStatusCode(400)
                .WithBody("""{"error":{"message":"strict function schemas are unsupported"}}"""));
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost()
                .WithBody(b => b != null && !b.Contains("\"strict\":true")))
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                model = "test-model",
                choices = new[] { new { message = new { content = "fallback ok" } } },
            }));

        var parameters = JsonDocument.Parse(
            """{"type":"object","properties":{},"additionalProperties":false}""").RootElement.Clone();
        var request = new LlmRequest("sys", "user", Tools:
        [
            new LlmToolDefinition("list_db_tables", "schema", parameters, Strict: true),
        ]);

        var response = await BuildClient().CompleteAsync(request, CancellationToken.None);

        response.Content.Should().Be("fallback ok");
        _server.LogEntries.Should().HaveCount(2);
        _server.LogEntries.First().RequestMessage.Body.Should().Contain("\"strict\":true");
        _server.LogEntries.Last().RequestMessage.Body.Should().NotContain("\"strict\":true");
    }

    [Fact]
    public async Task CompleteAsync_WithConversation_EmitsSystemPlusTurnsInOrder()
    {
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   model = "test-model",
                   choices = new[] { new { message = new { content = "ok" } } },
               }));

        var client = BuildClient();
        await client.CompleteAsync(
            new LlmRequest("SYSTEM", UserPrompt: "ignored", Conversation: new[]
            {
                new LlmMessage("user", "first question"),
                new LlmMessage("assistant", "first answer"),
                new LlmMessage("user", "second question"),
            }),
            CancellationToken.None);

        var body = _server.LogEntries.Single().RequestMessage.Body!;
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages");

        messages.GetArrayLength().Should().Be(4); // system + 3 turns
        messages[0].GetProperty("role").GetString().Should().Be("system");
        messages[0].GetProperty("content").GetString().Should().Be("SYSTEM");
        messages[1].GetProperty("role").GetString().Should().Be("user");
        messages[1].GetProperty("content").GetString().Should().Be("first question");
        messages[2].GetProperty("role").GetString().Should().Be("assistant");
        messages[3].GetProperty("content").GetString().Should().Be("second question");
        // UserPrompt is ignored once a Conversation is set.
        body.Should().NotContain("ignored");
    }

    [Fact]
    public async Task CompleteAsync_SlowerThanTimeout_ThrowsTimeout()
    {
        // WireMock responds after 5s, client timeout is 1s.
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithDelay(TimeSpan.FromSeconds(5))
                   .WithBodyAsJson(new
                   {
                       model = "test-model",
                       choices = new[] { new { message = new { content = "too late" } } },
                   }));

        var client = BuildClient(timeoutSeconds: 1);
        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            client.CompleteAsync(new LlmRequest("sys", "user"), CancellationToken.None));

        ex.Kind.Should().Be(LlmErrorKind.Timeout);
    }

    // ---- Streaming (SSE) ------------------------------------------------------------

    private const string SseBody =
        "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}],\"model\":\"test-model\"}\n\n" +
        "data: {\"choices\":[{\"delta\":{\"content\":\" world\"}}],\"model\":\"test-model\"}\n\n" +
        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2}}\n\n" +
        "data: [DONE]\n\n";

    private static async Task<(List<string> deltas, LlmStreamEvent? done)> Collect(
        OpenAiCompatibleLlmClient client, LlmRequest request)
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
    public async Task StreamAsync_HappyPath_YieldsDeltasThenDoneWithUsage()
    {
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "text/event-stream").WithBody(SseBody));

        var (deltas, done) = await Collect(BuildClient(), new LlmRequest("sys", "user"));

        string.Join("", deltas).Should().Be("Hello world");
        done.Should().NotBeNull();
        done!.Model.Should().Be("test-model");
        done.PromptTokens.Should().Be(5);
        done.CompletionTokens.Should().Be(2);
    }

    [Fact]
    public async Task StreamAsync_StreamOptions400_RetriesWithoutStreamOptions()
    {
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost()
                .WithBody(b => b != null && b.Contains("stream_options")))
               .RespondWith(Response.Create().WithStatusCode(400).WithBody("{\"error\":\"stream_options unsupported\"}"));
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost()
                .WithBody(b => b != null && !b.Contains("stream_options")))
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "text/event-stream").WithBody(SseBody));

        var (deltas, _) = await Collect(BuildClient(), new LlmRequest("sys", "user"));

        string.Join("", deltas).Should().Be("Hello world");
    }

    [Fact]
    public async Task StreamAsync_MaxTokensUnsupported400_RetriesWithMaxCompletionTokens()
    {
        // Streaming counterpart: max_tokens-400 → retry with max_completion_tokens, then SSE.
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost()
                .WithBody(b => b != null && b.Contains("max_tokens") && !b.Contains("max_completion_tokens")))
               .RespondWith(Response.Create().WithStatusCode(400)
                   .WithBody("{\"error\":{\"message\":\"Use 'max_completion_tokens' instead.\",\"param\":\"max_tokens\",\"code\":\"unsupported_parameter\"}}"));
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost()
                .WithBody(b => b != null && b.Contains("max_completion_tokens")))
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "text/event-stream").WithBody(SseBody));

        var (deltas, _) = await Collect(BuildClient(), new LlmRequest("sys", "user"));

        string.Join("", deltas).Should().Be("Hello world");
    }

    [Fact]
    public async Task StreamAsync_Non200_ThrowsClassifiedException()
    {
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(401).WithBody("invalid api key"));

        var client = BuildClient();
        var ex = await Assert.ThrowsAsync<LlmException>(async () =>
        {
            await foreach (var _ in client.StreamAsync(new LlmRequest("sys", "user"), CancellationToken.None)) { }
        });
        ex.Kind.Should().Be(LlmErrorKind.Unauthorized);
    }

    // ---- Tool-Calling ---------------------------------------------------------------

    private static LlmToolDefinition Tool(string name)
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{"type":"object","properties":{}}""");
        return new LlmToolDefinition(name, $"desc for {name}", doc.RootElement.Clone());
    }

    private static string Sse(params string[] dataLines) =>
        string.Concat(dataLines.Select(d => $"data: {d}\n\n"));

    [Fact]
    public async Task CompleteAsync_WithTools_SendsToolsAndToolChoiceInBody()
    {
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   model = "test-model",
                   choices = new[] { new { message = new { content = "ok" } } },
               }));

        await BuildClient().CompleteAsync(
            new LlmRequest("sys", "user", Tools: new[] { Tool("analyze_workflow") }, ToolChoice: "auto"),
            CancellationToken.None);

        var body = _server.LogEntries.Single().RequestMessage.Body!;
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var tools = doc.RootElement.GetProperty("tools");
        tools.GetArrayLength().Should().Be(1);
        tools[0].GetProperty("type").GetString().Should().Be("function");
        tools[0].GetProperty("function").GetProperty("name").GetString().Should().Be("analyze_workflow");
        doc.RootElement.GetProperty("tool_choice").GetString().Should().Be("auto");
    }

    [Fact]
    public async Task CompleteAsync_ToolCallResponse_ParsesToolCallsAndFinishReason_ContentNull()
    {
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   model = "test-model",
                   choices = new[] { new
                   {
                       finish_reason = "tool_calls",
                       message = new
                       {
                           role = "assistant",
                           content = (string?)null,
                           tool_calls = new[] { new
                           {
                               id = "call_1", type = "function",
                               function = new { name = "analyze_workflow", arguments = "{\"x\":1}" },
                           } },
                       },
                   } },
               }));

        var resp = await BuildClient().CompleteAsync(new LlmRequest("sys", "user"), CancellationToken.None);

        resp.FinishReason.Should().Be("tool_calls");
        resp.Content.Should().BeEmpty();
        resp.ToolCalls.Should().ContainSingle();
        resp.ToolCalls![0].Id.Should().Be("call_1");
        resp.ToolCalls[0].Name.Should().Be("analyze_workflow");
        resp.ToolCalls[0].ArgumentsJson.Should().Be("{\"x\":1}");
    }

    [Fact]
    public async Task CompleteAsync_SerializesAssistantToolCallAndToolResultTurns()
    {
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   model = "test-model",
                   choices = new[] { new { message = new { content = "done" } } },
               }));

        await BuildClient().CompleteAsync(
            new LlmRequest("SYS", "ignored", Conversation: new[]
            {
                new LlmMessage("user", "analyze it"),
                new LlmMessage("assistant", "", ToolCalls: new[] { new LlmToolCall("call_1", "analyze_workflow", "{}") }),
                new LlmMessage("tool", "{\"ok\":true}", ToolCallId: "call_1"),
            }),
            CancellationToken.None);

        var body = _server.LogEntries.Single().RequestMessage.Body!;
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages");
        messages.GetArrayLength().Should().Be(4); // system + 3 turns

        var assistant = messages[2];
        assistant.GetProperty("role").GetString().Should().Be("assistant");
        var calls = assistant.GetProperty("tool_calls");
        calls[0].GetProperty("id").GetString().Should().Be("call_1");
        calls[0].GetProperty("function").GetProperty("name").GetString().Should().Be("analyze_workflow");

        var toolTurn = messages[3];
        toolTurn.GetProperty("role").GetString().Should().Be("tool");
        toolTurn.GetProperty("tool_call_id").GetString().Should().Be("call_1");
        toolTurn.GetProperty("content").GetString().Should().Be("{\"ok\":true}");
    }

    [Fact]
    public async Task StreamAsync_AccumulatesToolCallsAcrossDeltas_AndEmitsFinishReason()
    {
        var sse = Sse(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"analyze_workflow","arguments":""}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"wo"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"rk\":1}"}}]}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""",
            "[DONE]");
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "text/event-stream").WithBody(sse));

        var (deltas, done) = await Collect(BuildClient(), new LlmRequest("sys", "user"));

        deltas.Should().BeEmpty(); // tool-call turn has no prose
        done.Should().NotBeNull();
        done!.FinishReason.Should().Be("tool_calls");
        done.ToolCalls.Should().ContainSingle();
        done.ToolCalls![0].Id.Should().Be("call_1");
        done.ToolCalls[0].Name.Should().Be("analyze_workflow");
        done.ToolCalls[0].ArgumentsJson.Should().Be("{\"work\":1}");
    }

    [Fact]
    public async Task StreamAsync_TwoParallelToolCalls_WithIndices_BothSurface()
    {
        // Canonical OpenAI parallel tool calling: two calls distinguished by `index` 0 and 1.
        var sse = Sse(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"search_docs","arguments":"{\"q\":\"a\"}"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":1,"id":"call_b","type":"function","function":{"name":"search_source","arguments":"{\"q\":\"b\"}"}}]}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""",
            "[DONE]");
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "text/event-stream").WithBody(sse));

        var (_, done) = await Collect(BuildClient(), new LlmRequest("sys", "user"));

        done!.ToolCalls.Should().HaveCount(2);
        done.ToolCalls![0].Name.Should().Be("search_docs");
        done.ToolCalls[0].ArgumentsJson.Should().Be("{\"q\":\"a\"}");
        done.ToolCalls[1].Name.Should().Be("search_source");
        done.ToolCalls[1].ArgumentsJson.Should().Be("{\"q\":\"b\"}");
    }

    [Fact]
    public async Task StreamAsync_TwoToolCalls_WithoutIndex_DoNotCollapse()
    {
        // LM Studio / llama.cpp style: parallel calls streamed WITHOUT the OpenAI `index` field.
        // The old accumulator defaulted the missing index to 0 → both collapsed into one corrupt call.
        var sse = Sse(
            """{"choices":[{"delta":{"tool_calls":[{"id":"call_a","type":"function","function":{"name":"search_docs","arguments":"{\"q\":\"a\"}"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"id":"call_b","type":"function","function":{"name":"search_source","arguments":"{\"q\":\"b\"}"}}]}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
            "[DONE]");
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "text/event-stream").WithBody(sse));

        var (_, done) = await Collect(BuildClient(), new LlmRequest("sys", "user"));

        done!.ToolCalls.Should().HaveCount(2);
        done.ToolCalls!.Select(t => t.Name).Should().BeEquivalentTo(new[] { "search_docs", "search_source" });
        done.ToolCalls.Single(t => t.Name == "search_docs").ArgumentsJson.Should().Be("{\"q\":\"a\"}");
        done.ToolCalls.Single(t => t.Name == "search_source").ArgumentsJson.Should().Be("{\"q\":\"b\"}");
    }

    [Fact]
    public async Task StreamAsync_IndexlessToolCall_SplitArguments_AppendToSameSlot()
    {
        // One index-less call whose arguments arrive across two chunks; the continuation fragment
        // carries neither id nor name → must append to the current slot, not open a new one.
        var sse = Sse(
            """{"choices":[{"delta":{"tool_calls":[{"id":"call_a","type":"function","function":{"name":"search_docs","arguments":"{\"q\":"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"function":{"arguments":"\"abc\"}"}}]}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
            "[DONE]");
        _server.Given(Request.Create().WithPath("/chat/completions").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "text/event-stream").WithBody(sse));

        var (_, done) = await Collect(BuildClient(), new LlmRequest("sys", "user"));

        done!.ToolCalls.Should().ContainSingle();
        done.ToolCalls![0].Name.Should().Be("search_docs");
        done.ToolCalls[0].ArgumentsJson.Should().Be("{\"q\":\"abc\"}");
    }

    /// <summary>
    /// Minimal IHttpClientFactory that always returns a fresh HttpClient with no handler
    /// pipeline. The real DI path would configure the named client "Llm" — the test doesn't
    /// need that since it only ever talks to WireMock.
    /// </summary>
    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient();
    }
}
