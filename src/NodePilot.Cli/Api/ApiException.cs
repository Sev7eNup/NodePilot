using System.Net;

namespace NodePilot.Cli.Api;

/// <summary>
/// Thrown by <see cref="NodePilotApiClient"/> on every non-2xx response. Captures both
/// the HTTP status (so commands can branch on 401/403/423) and the parsed
/// <c>ProblemDetails</c> payload when the server returned one.
/// </summary>
public sealed class ApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string? Title { get; }
    public string? Detail { get; }
    public string? RawBody { get; }

    public ApiException(HttpStatusCode statusCode, string? title, string? detail, string? rawBody)
        : base(BuildMessage(statusCode, title, detail, rawBody))
    {
        StatusCode = statusCode;
        Title = title;
        Detail = detail;
        RawBody = rawBody;
    }

    private static string BuildMessage(HttpStatusCode status, string? title, string? detail, string? body)
    {
        var label = title ?? status.ToString();
        if (!string.IsNullOrWhiteSpace(detail)) return $"{(int)status} {label}: {detail}";
        if (!string.IsNullOrWhiteSpace(body) && body.Length < 400) return $"{(int)status} {label}: {body}";
        return $"{(int)status} {label}";
    }

    /// <summary>True for 401 — caller must re-authenticate.</summary>
    public bool IsUnauthorized => StatusCode == HttpStatusCode.Unauthorized;

    /// <summary>True for 403 — caller authenticated but lacks the required role.</summary>
    public bool IsForbidden => StatusCode == HttpStatusCode.Forbidden;

    /// <summary>True for 423 — workflow is checked out by another user.</summary>
    public bool IsLocked => (int)StatusCode == 423;

    /// <summary>True for 409 — conflicting state (e.g. lock contention, idempotency).</summary>
    public bool IsConflict => StatusCode == HttpStatusCode.Conflict;
}
