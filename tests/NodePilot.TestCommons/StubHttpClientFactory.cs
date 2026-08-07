namespace NodePilot.TestCommons;

/// <summary>
/// Minimal <see cref="IHttpClientFactory"/> stub. Without a handler it hands out a plain
/// <see cref="HttpClient"/> — enough for code paths that only need the factory dependency
/// satisfied and never actually send. With a handler (e.g. <see cref="StubHttpMessageHandler"/>)
/// the produced clients route through it, so probe/transport tests can script responses.
/// Lives here because Ai.Tests, Api.Tests and Engine.Tests each carried a private copy.
/// </summary>
public sealed class StubHttpClientFactory(HttpMessageHandler? handler = null) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
}
