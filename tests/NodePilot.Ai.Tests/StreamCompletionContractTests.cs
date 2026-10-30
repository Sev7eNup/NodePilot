using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace NodePilot.Ai.Tests;

public sealed class StreamCompletionContractTests
{
    [Fact]
    public async Task UpstreamFailureBody_IsNotWrittenToLogs()
    {
        const string body = "private upstream diagnostic from the request";
        var logger = new Mock<ILogger<OpenAiCompatibleLlmClient>>();
        var config = new LlmClientConfig(LlmEndpointGuard.ResolveEndpoint("https://llm.example.test/v1"),
            null, "test", 32, null, 10);
        var client = new OpenAiCompatibleLlmClient(new StubFactory(body, HttpStatusCode.InternalServerError), config, logger.Object);
        var run = () => client.CompleteAsync(new LlmRequest("system", "question"), CancellationToken.None);
        (await run.Should().ThrowAsync<LlmException>()).Which.BodyExcerpt.Should().Be(body);
        var logs = logger.Invocations.Where(i => i.Method.Name == "Log").ToList();
        logs.Should().NotBeEmpty();
        logs.Should().OnlyContain(i => !i.Arguments[2].ToString()!.Contains(body) && i.Arguments[3] == null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EofAfterDelta_DoesNotEmitSuccess(bool responses)
    {
        var delta = responses
            ? """{"type":"response.output_text.delta","delta":"partial"}"""
            : """{"choices":[{"index":0,"delta":{"content":"partial"}}]}""";
        var client = Client(responses, "data: " + delta + "\n\n");
        var events = new List<LlmStreamEvent>();
        var run = async () =>
        {
            await foreach (var evt in client.StreamAsync(new LlmRequest("system", "question"), CancellationToken.None))
                events.Add(evt);
        };
        (await run.Should().ThrowAsync<LlmException>()).Which.Kind.Should().Be(LlmErrorKind.MalformedResponse);
        events.Should().Contain(e => e.ContentDelta == "partial");
        events.Should().NotContain(e => e.Done);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitTerminalEvent_EmitsDone(bool responses)
    {
        var terminal = responses
            ? "data: " + """{"type":"response.completed","response":{"status":"completed","model":"test"}}""" + "\n\n"
            : "data: " + """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""" + "\n\ndata: [DONE]\n\n";
        var events = new List<LlmStreamEvent>();
        await foreach (var evt in Client(responses, terminal)
            .StreamAsync(new LlmRequest("system", "question"), CancellationToken.None)) events.Add(evt);
        events.Should().ContainSingle(e => e.Done);
    }

    private static ILlmClient Client(bool responses, string body)
    {
        var config = new LlmClientConfig(LlmEndpointGuard.ResolveEndpoint(
            responses ? "https://llm.example.test/v1/responses" : "https://llm.example.test/v1"),
            null, "test", 32, null, 10);
        var factory = new StubFactory(body);
        return responses ? new OpenAiResponsesLlmClient(factory, config, NullLogger<OpenAiResponsesLlmClient>.Instance)
            : new OpenAiCompatibleLlmClient(factory, config, NullLogger<OpenAiCompatibleLlmClient>.Instance);
    }

    private sealed class StubFactory(string body, HttpStatusCode status = HttpStatusCode.OK) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(body, status));
    }

    private sealed class Handler(string body, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") });
    }
}
