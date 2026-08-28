namespace NodePilot.Ai;

/// <summary>
/// Classification of LLM call failures. The controller maps these kinds to HTTP status codes:
/// <c>Unreachable</c>, <c>Timeout</c>, <c>RateLimited</c> and <c>Unauthorized</c> become 503,
/// <c>UpstreamError</c> and <c>MalformedResponse</c> become 502, so a failing upstream is not
/// reported as a fault of the NodePilot API itself.
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
