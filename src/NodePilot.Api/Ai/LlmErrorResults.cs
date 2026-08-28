using Microsoft.AspNetCore.Mvc;
using NodePilot.Ai;

namespace NodePilot.Api.Ai;

/// <summary>
/// The single <see cref="LlmException"/> to HTTP mapping for the JSON (non-SSE) AI
/// endpoints, plus the 503/502 result helpers the AI controllers share. Codes come
/// from <see cref="LlmErrorCodes"/> so the JSON path and the SSE <c>error</c> events
/// always report the same code.
/// </summary>
internal static class LlmErrorResults
{
    /// <summary>
    /// Maps by <see cref="LlmException.Kind"/>: infrastructure problems (unreachable,
    /// timeout, auth, rate limit) become 503 so clients may retry after fixing config;
    /// a bad upstream response (malformed body, invalid JSON) becomes 502.
    /// <see cref="LlmException.BodyExcerpt"/> is included for <c>UpstreamError</c> so the
    /// user sees the real upstream error message instead of only the HTTP status.
    /// </summary>
    public static ActionResult MapLlmException(
        this ControllerBase controller, ILogger logger, LlmException ex, string logContext)
    {
        logger.LogWarning(ex, "{LlmCallContext} failed: {Kind}", logContext, ex.Kind);
        var code = LlmErrorCodes.For(ex);
        return ex.Kind switch
        {
            LlmErrorKind.Unreachable => controller.LlmServiceUnavailable(code, ex.Message),
            LlmErrorKind.Timeout => controller.LlmServiceUnavailable(code, ex.Message),
            LlmErrorKind.Unauthorized => controller.LlmServiceUnavailable(code,
                "LLM endpoint rejected the configured API key. Check Llm:ApiKey."),
            LlmErrorKind.RateLimited => controller.LlmServiceUnavailable(code,
                "LLM endpoint rate-limited the request. Try again shortly."),
            LlmErrorKind.MalformedResponse => controller.LlmBadGateway(code, ex.Message, ex.BodyExcerpt),
            LlmErrorKind.UpstreamError => controller.LlmBadGateway(code,
                // Not every upstream error carries an HTTP status: the Responses API reports a
                // failed run inside an HTTP 200 body, so fall back to the exception message.
                ex.HttpStatus is int status ? $"LLM endpoint returned HTTP {status}." : ex.Message,
                ex.BodyExcerpt),
            _ => controller.StatusCode(StatusCodes.Status500InternalServerError,
                new { code, message = ex.Message }),
        };
    }

    public static ObjectResult LlmServiceUnavailable(this ControllerBase controller, string code, string message)
        => controller.StatusCode(StatusCodes.Status503ServiceUnavailable, new { code, message });

    public static ObjectResult LlmBadGateway(
        this ControllerBase controller, string code, string message, string? bodyExcerpt = null)
        => controller.StatusCode(StatusCodes.Status502BadGateway,
            bodyExcerpt is null ? (object)new { code, message } : new { code, message, bodyExcerpt });
}
