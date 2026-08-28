using NodePilot.Ai;

namespace NodePilot.Api.Ai;

/// <summary>
/// Error codes for the AI endpoints. Shared by the HTTP mapping (<c>MapLlmException</c>) and the
/// SSE <c>error</c> events so both paths report the same code.
/// </summary>
internal static class LlmErrorCodes
{
    public static string For(LlmException ex) => ex.Kind switch
    {
        LlmErrorKind.Unreachable => "LLM_UNREACHABLE",
        LlmErrorKind.Timeout => "LLM_TIMEOUT",
        LlmErrorKind.Unauthorized => "LLM_UNAUTHORIZED",
        LlmErrorKind.RateLimited => "LLM_RATE_LIMITED",
        LlmErrorKind.MalformedResponse => "LLM_MALFORMED_RESPONSE",
        LlmErrorKind.UpstreamError => "LLM_UPSTREAM_ERROR",
        _ => "LLM_UNKNOWN",
    };
}
