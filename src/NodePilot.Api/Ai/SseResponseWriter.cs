using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace NodePilot.Api.Ai;

/// <summary>
/// Writes Server-Sent-Events directly to the <see cref="HttpResponse.Body"/> (same pattern as
/// the CSV/NDJSON exports in <c>AuditController</c>). Shared by the streaming AI endpoints
/// (chat + script generation). Sets the SSE headers in <see cref="Begin"/> — once that happens
/// the response headers are committed, so the controller peeks at the first event
/// <b>beforehand</b> in order to still return pre-stream errors as a normal HTTP status.
/// </summary>
internal sealed class SseResponseWriter : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly StreamWriter _writer;

    private SseResponseWriter(StreamWriter writer) => _writer = writer;

    public static SseResponseWriter Begin(HttpResponse response)
    {
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no"; // tell nginx/reverse proxies not to buffer this response
        var writer = new StreamWriter(response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new SseResponseWriter(writer);
    }

    /// <summary>Writes one event as <c>event: name\ndata: &lt;json&gt;\n\n</c> and flushes immediately.</summary>
    public async Task WriteAsync(string eventName, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        await _writer.WriteAsync($"event: {eventName}\ndata: {json}\n\n".AsMemory(), ct);
        await _writer.FlushAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        try { await _writer.DisposeAsync(); }
        catch { /* the client may have disconnected mid-flush — Dispose must never throw */ }
    }
}
