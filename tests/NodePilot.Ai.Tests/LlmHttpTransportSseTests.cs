using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NodePilot.Ai.Tests;

/// <summary>
/// SSE reading through <see cref="LlmHttpTransport.ReadSseDataAsync"/>. The response byte cap
/// sits below the line reader, so an oversized line trips it while it is being read instead of
/// after the whole line was buffered in memory.
/// </summary>
public sealed class LlmHttpTransportSseTests
{
    private static LlmHttpTransport Transport() => new(
        new FactoryStub(),
        new LlmClientConfig(
            Endpoint: LlmEndpointGuard.ResolveEndpoint("https://llm.example.test/v1"),
            ApiKey: null,
            Model: "test-model",
            MaxTokens: 16,
            Temperature: null,
            TimeoutSeconds: 90),
        NullLogger.Instance);

    [Fact]
    public async Task ReadSseDataAsync_YieldsDataPayloads_AndStopsAtDone()
    {
        const string body = "event: x\ndata: {\"a\":1}\n\ndata:   {\"b\":2}\ndata: [DONE]\ndata: {\"c\":3}\n";
        using var resp = new HttpResponseMessage { Content = new StringContent(body) };

        var payloads = new List<string>();
        await foreach (var data in Transport().ReadSseDataAsync(resp, CancellationToken.None, CancellationToken.None))
            payloads.Add(data);

        payloads.Should().Equal("{\"a\":1}", "{\"b\":2}");
    }

    [Fact]
    public async Task ReadSseDataAsync_OversizedLineWithoutNewline_TripsTheCapWhileReading()
    {
        var upstream = new UnterminatedLineStream(totalBytes: LlmHttpTransport.MaxResponseBytes * 4);
        using var resp = new HttpResponseMessage { Content = new StreamContent(upstream) };

        var act = async () =>
        {
            await foreach (var _ in Transport().ReadSseDataAsync(resp, CancellationToken.None, CancellationToken.None)) { }
        };

        var thrown = await act.Should().ThrowAsync<LlmException>();
        thrown.Which.Kind.Should().Be(LlmErrorKind.MalformedResponse);
        upstream.BytesRead.Should().BeLessThan(LlmHttpTransport.MaxResponseBytes + 64 * 1024,
            "the reader must stop at the cap, not after buffering the whole line");
    }

    /// <summary>A body that is one endless line; records how much of it was pulled.</summary>
    private sealed class UnterminatedLineStream(long totalBytes) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => totalBytes;
        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, totalBytes - BytesRead);
            buffer.AsSpan(offset, n).Fill((byte)'a');
            BytesRead += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FactoryStub : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
