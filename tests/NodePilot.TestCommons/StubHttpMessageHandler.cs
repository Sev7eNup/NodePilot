namespace NodePilot.TestCommons;

/// <summary>
/// Func-based <see cref="HttpMessageHandler"/> for tests that need an <see cref="HttpClient"/>
/// without touching the network: the constructor delegate produces (or throws) the response,
/// and every request is recorded in <see cref="Requests"/> for later assertions. Shared here
/// because plain stub/throwing/capturing handler copies existed across Api.Tests and
/// Engine.Tests; handlers with genuinely bespoke behavior (sequenced responses, streaming
/// bodies) stay local to their test file.
/// </summary>
public sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    /// <summary>Every request seen by the handler, in send order.</summary>
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(responder(request));
    }
}
