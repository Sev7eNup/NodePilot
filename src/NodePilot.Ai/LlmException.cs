namespace NodePilot.Ai;

/// <summary>
/// Classification of LLM call failures. The controller maps these kinds to HTTP status codes
/// (<c>Unreachable</c>/<c>Timeout</c>/<c>RateLimited</c> → 503, <c>Unauthorized</c> → 503 with a
/// clear code property, <c>UpstreamError</c>/<c>MalformedResponse</c> → 502) — without making the
/// NodePilot API endpoint itself look like it's returning 5xx errors, since the NodePilot server
/// itself is running fine; it's the upstream LLM that failed.
/// </summary>
public enum LlmErrorKind
{
    Unreachable,
    Timeout,
    Unauthorized,
    RateLimited,
    UpstreamError,
    MalformedResponse,
}

/// <summary>
/// Thrown by <see cref="ILlmClient"/> for every error path. Carries the classified kind, an
/// optional HTTP status, and a short body excerpt for diagnostics.
/// </summary>
public sealed class LlmException : Exception
{
    public LlmErrorKind Kind { get; }
    public int? HttpStatus { get; }
    public string? BodyExcerpt { get; }

    public LlmException(LlmErrorKind kind, string message, int? httpStatus = null, string? bodyExcerpt = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        HttpStatus = httpStatus;
        BodyExcerpt = bodyExcerpt;
    }
}
