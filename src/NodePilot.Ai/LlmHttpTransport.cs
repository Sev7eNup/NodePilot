using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NodePilot.Ai;

/// <summary>
/// The HTTP plumbing both wire dialects share: POST with bearer auth against
/// <see cref="LlmEndpointTarget.PostUrl"/>, the timeout scope, the classified error mapping, the
/// response-size caps, and SSE line framing. Everything dialect-specific (request body, response
/// shape, stream events) stays in the <see cref="ILlmClient"/> implementations —
/// <see cref="OpenAiCompatibleLlmClient"/> and <see cref="OpenAiResponsesLlmClient"/>.
/// </summary>
internal sealed class LlmHttpTransport
{
    private const int BodyExcerptMaxChars = 500;

    // Cap upstream response bodies before parsing so a hostile or runaway LLM endpoint cannot
    // exhaust memory by streaming very large payloads into JsonDocument.ParseAsync. The limit sits
    // well above any realistic chat-completion payload.
    internal const long MaxResponseBytes = 16L * 1024 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LlmClientConfig _config;
    private readonly ILogger _logger;

    public LlmHttpTransport(IHttpClientFactory httpClientFactory, LlmClientConfig config, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Creates a timeout scope linked to the caller's token, so the caller can still cancel and
    /// HttpClient.Timeout stays out of the way. Non-streaming: one scope per attempt. Streaming:
    /// one scope for the whole stream, so the read loop has to stay inside it.
    /// </summary>
    public CancellationTokenSource CreateTimeoutScope(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));
        return cts;
    }

    /// <summary>
    /// POSTs <paramref name="body"/> as JSON. Throws a classified <see cref="LlmException"/> for a
    /// timeout, an unreachable endpoint, or any non-2xx status (the response is disposed in that
    /// case). On success the caller owns — and must dispose — the returned response.
    /// </summary>
    /// <param name="io">The timeout-scoped token every I/O operation runs under.</param>
    /// <param name="caller">
    /// The caller's token distinguishes a timeout from genuine caller-side cancellation:
    /// when the caller cancelled, the <see cref="OperationCanceledException"/> propagates
    /// untouched.
    /// </param>
    public async Task<HttpResponseMessage> SendAsync(
        Dictionary<string, object?> body,
        HttpCompletionOption completionOption,
        CancellationToken io,
        CancellationToken caller)
    {
        var http = _httpClientFactory.CreateClient(LlmHttpClient.Name);

        using var req = new HttpRequestMessage(HttpMethod.Post, _config.Endpoint.PostUrl)
        {
            Content = JsonContent.Create(body),
        };
        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        }

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, completionOption, io);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            // Connecting has its own, much shorter budget (LlmConnectGuard), so once this fires the
            // request is on the wire and the model is still generating.
            throw new LlmException(LlmErrorKind.Timeout,
                $"LLM endpoint accepted the request but sent no answer within {_config.TimeoutSeconds}s "
                + $"({_config.Endpoint.PostUrl}). The connection itself was fine — raise the profile's "
                + "timeout if the model needs longer, or pick a faster model.");
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException(LlmErrorKind.Unreachable, DescribeUnreachable(ex), inner: ex);
        }

        if (!resp.IsSuccessStatusCode)
            await ThrowUpstreamAsync(resp, io);

        return resp;
    }

    /// <summary>
    /// Turns a transport failure into a message that names which stage failed. The stages are
    /// separable because each has its own deadline: DNS and TCP fail inside <c>LlmConnectGuard</c>
    /// with a message that already names them, certificate validation raises
    /// <see cref="AuthenticationException"/>, and the handler's <c>ConnectTimeout</c> can only fire
    /// once those two have passed, so it means the TLS handshake stalled.
    /// </summary>
    internal string DescribeUnreachable(Exception ex)
    {
        var url = _config.Endpoint.PostUrl;

        Exception innermost = ex;
        while (innermost.InnerException is not null) innermost = innermost.InnerException;

        if (innermost is AuthenticationException auth)
        {
            return $"LLM endpoint TLS ({url}): the server's certificate was rejected — {auth.Message} "
                 + "Import the issuing CA into the machine's Trusted Root store on the NodePilot host; "
                 + "a certificate that a browser accepts on a workstation is not automatically trusted by the service account.";
        }

        // TimeoutException here is SocketsHttpHandler.ConnectTimeout. DNS and TCP carry shorter
        // deadlines of their own, so they can never be what expired.
        if (innermost is TimeoutException)
        {
            return $"LLM endpoint TLS ({url}): the TCP connection was established but the TLS handshake did not "
                 + $"complete within {LlmConnectGuard.HandshakeTimeout.TotalSeconds:0}s. Typical causes are an endpoint "
                 + "demanding a client certificate, an SNI mismatch, or a middlebox that accepts the connection and "
                 + "never negotiates.";
        }

        // Messages from the connect guard already name their stage, so they pass through unwrapped.
        return innermost is IOException io && io.Message.StartsWith("LLM endpoint ", StringComparison.Ordinal)
            ? io.Message
            : $"LLM endpoint unreachable ({url}): {innermost.Message}";
    }

    /// <summary>
    /// Reads an error response's body excerpt (through the same byte cap as the success path) and
    /// throws the matching <see cref="LlmException"/>. Disposes <paramref name="resp"/>.
    /// </summary>
    private async Task ThrowUpstreamAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        string bodyText;
        try
        {
            await using var rawStream = await resp.Content.ReadAsStreamAsync(ct);
            await using var stream = new LengthLimitedStream(rawStream, MaxResponseBytes);
            bodyText = await ReadBodyExcerptAsync(stream, ct);
        }
        catch
        {
            bodyText = "<body unreadable>";
        }

        var status = (int)resp.StatusCode;
        var kind = resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized => LlmErrorKind.Unauthorized,
            HttpStatusCode.Forbidden => LlmErrorKind.Unauthorized,
            HttpStatusCode.TooManyRequests => LlmErrorKind.RateLimited,
            _ => LlmErrorKind.UpstreamError,
        };
        _logger.LogWarning("LLM upstream returned {Status} for model {Model}: {BodyExcerpt}",
            status, _config.Model, bodyText);
        resp.Dispose();
        throw new LlmException(kind, $"LLM-Endpoint antwortete mit HTTP {status}.",
            httpStatus: status, bodyExcerpt: bodyText);
    }

    /// <summary>
    /// Parses a success response body into a <see cref="JsonDocument"/> (the caller owns it), under
    /// the response byte cap. The Content-Length check rejects an oversized response without
    /// reading the body; an upstream that omits Content-Length is still bounded by
    /// <see cref="LengthLimitedStream"/>.
    /// </summary>
    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.Content.Headers.ContentLength is long cl && cl > MaxResponseBytes)
        {
            throw new LlmException(LlmErrorKind.MalformedResponse,
                $"LLM-Antwort überschreitet das Body-Limit ({cl} > {MaxResponseBytes} bytes).",
                httpStatus: (int)resp.StatusCode);
        }

        await using var rawStream = await resp.Content.ReadAsStreamAsync(ct);
        await using var stream = new LengthLimitedStream(rawStream, MaxResponseBytes);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Body-Limit", StringComparison.Ordinal))
        {
            // LengthLimitedStream tripped: upstream sent more than MaxResponseBytes.
            throw new LlmException(LlmErrorKind.MalformedResponse, ex.Message, inner: ex);
        }
        catch (JsonException ex)
        {
            throw new LlmException(LlmErrorKind.MalformedResponse,
                "LLM-Antwort war kein valides JSON.", inner: ex);
        }
    }

    /// <summary>
    /// Yields the payload of every <c>data:</c> line of an SSE response, skipping blank lines and
    /// stopping at the <c>[DONE]</c> sentinel (Chat Completions sends it; the Responses API simply
    /// ends the stream). Enforces the response byte cap across the whole stream, which
    /// <see cref="LengthLimitedStream"/> does not cover here.
    /// </summary>
    public async IAsyncEnumerable<string> ReadSseDataAsync(
        HttpResponseMessage resp,
        [EnumeratorCancellation] CancellationToken io,
        CancellationToken caller)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(io);
        using var reader = new StreamReader(stream);

        long totalBytes = 0;
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(io);
            }
            catch (OperationCanceledException) when (!caller.IsCancellationRequested)
            {
                throw new LlmException(LlmErrorKind.Timeout,
                    $"LLM stream stalled for more than {_config.TimeoutSeconds}s ({_config.Endpoint.PostUrl}).");
            }
            if (line is null) break;

            totalBytes += line.Length;
            if (totalBytes > MaxResponseBytes)
                throw new LlmException(LlmErrorKind.MalformedResponse,
                    $"LLM-Stream überschreitet das Body-Limit ({MaxResponseBytes} bytes).");

            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0) continue;
            if (data == "[DONE]") break;

            yield return data;
        }
    }

    private static async Task<string> ReadBodyExcerptAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            using var sr = new StreamReader(stream);
            var raw = await sr.ReadToEndAsync(ct);
            return raw.Length > BodyExcerptMaxChars
                ? raw[..BodyExcerptMaxChars] + "…"
                : raw;
        }
        catch
        {
            return "<body unreadable>";
        }
    }
}

/// <summary>
/// Const container for the named HttpClient's name. Registered in
/// <see cref="LlmServiceCollectionExtensions.AddNodePilotAi"/> and resolved by the
/// <see cref="ILlmClient"/> implementations via <see cref="IHttpClientFactory"/>.
/// </summary>
public static class LlmHttpClient
{
    public const string Name = "Llm";
}

/// <summary>
/// Read-only stream wrapper that throws after <paramref name="maxBytes"/> have been read. Protects
/// <see cref="JsonDocument.ParseAsync(Stream, JsonDocumentOptions, CancellationToken)"/> from
/// oversized upstream responses when the LLM endpoint omits Content-Length or reports it wrongly.
/// </summary>
internal sealed class LengthLimitedStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maxBytes;
    private long _read;

    public LengthLimitedStream(Stream inner, long maxBytes) { _inner = inner; _maxBytes = maxBytes; }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        Advance(n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await _inner.ReadAsync(buffer, ct);
        Advance(n);
        return n;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var n = await _inner.ReadAsync(buffer.AsMemory(offset, count), ct);
        Advance(n);
        return n;
    }

    private void Advance(int n)
    {
        _read += n;
        if (_read > _maxBytes)
            throw new InvalidOperationException(
                $"LLM-Antwort überschreitet das Body-Limit ({_read} > {_maxBytes} bytes).");
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
