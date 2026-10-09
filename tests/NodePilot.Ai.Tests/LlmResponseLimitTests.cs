using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NodePilot.Ai.Tests;

public sealed class LlmResponseLimitTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CompleteAsync_OversizedBody_StopsReadingAtLimit(bool responses, bool error)
    {
        using var body = new CountingBody(LlmHttpTransport.MaxResponseBytes * 4);
        using var http = new HttpClient(new ResponseHandler(body, error ? HttpStatusCode.BadGateway : HttpStatusCode.OK));
        var client = Client(http, responses);

        var act = () => client.CompleteAsync(new LlmRequest("system", "hello", false), TestContext.Current.CancellationToken);

        var exception = (await act.Should().ThrowAsync<LlmException>()).Which;
        exception.Kind.Should().Be(error ? LlmErrorKind.UpstreamError : LlmErrorKind.MalformedResponse);
        body.BytesRead.Should().BeLessThanOrEqualTo(LlmHttpTransport.MaxResponseBytes + 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteAsync_OversizedContentLength_DoesNotReadBody(bool responses)
    {
        using var body = new CountingBody(LlmHttpTransport.MaxResponseBytes * 4);
        using var http = new HttpClient(new ResponseHandler(body, HttpStatusCode.OK, bodyLength: LlmHttpTransport.MaxResponseBytes * 4));

        var act = () => Client(http, responses).CompleteAsync(new LlmRequest("system", "hello", false), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<LlmException>()).Which.Kind.Should().Be(LlmErrorKind.MalformedResponse);
        body.BytesRead.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteAsync_BodyStalls_ProfileTimeoutStillApplies(bool responses)
    {
        using var body = new StalledBody();
        using var http = new HttpClient(new ResponseHandler(body, HttpStatusCode.OK));

        var act = () => Client(http, responses, timeoutSeconds: 1).CompleteAsync(
            new LlmRequest("system", "hello", false), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<LlmException>()).Which.Kind.Should().Be(LlmErrorKind.Timeout);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteAsync_CallerCancelsDuringBodyRead_PreservesCancellation(bool responses)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var body = new StalledBody(cancellation);
        using var http = new HttpClient(new ResponseHandler(body, HttpStatusCode.OK));

        var act = () => Client(http, responses).CompleteAsync(new LlmRequest("system", "hello", false), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static ILlmClient Client(HttpClient http, bool responses, int timeoutSeconds = 30)
    {
        var config = new LlmClientConfig(LlmEndpointGuard.ResolveEndpoint(
            responses ? "https://llm.example.test/v1/responses" : "https://llm.example.test/v1"),
            null, "test-model", 100, null, timeoutSeconds);
        return responses
            ? new OpenAiResponsesLlmClient(new Factory(http), config, NullLogger<OpenAiResponsesLlmClient>.Instance)
            : new OpenAiCompatibleLlmClient(new Factory(http), config, NullLogger<OpenAiCompatibleLlmClient>.Instance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteAsync_BodyConnectionFails_PreservesTransportClassification(bool responses)
    {
        using var body = new FailedBody();
        using var http = new HttpClient(new ResponseHandler(body, HttpStatusCode.OK));

        var act = () => Client(http, responses).CompleteAsync(new LlmRequest("system", "hello", false), TestContext.Current.CancellationToken);

        var failure = (await act.Should().ThrowAsync<LlmException>()).Which;
        failure.Kind.Should().Be(LlmErrorKind.Unreachable);
        failure.InnerException.Should().BeOfType<HttpIOException>();
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ResponseHandler(Stream body, HttpStatusCode status, long? bodyLength = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(body);
            content.Headers.ContentLength = bodyLength;
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }

    private class CountingBody(long size) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, size - BytesRead);
            buffer.AsSpan(offset, read).Fill((byte)' ');
            BytesRead += read;
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StalledBody(CancellationTokenSource? cancelOnRead = null) : CountingBody(0)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancelOnRead?.Cancel();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private sealed class FailedBody : CountingBody
    {
        public FailedBody() : base(0) { }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new HttpIOException(HttpRequestError.ResponseEnded, "Response ended prematurely."));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
