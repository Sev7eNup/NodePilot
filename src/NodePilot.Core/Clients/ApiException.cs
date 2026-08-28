using System.Net;

namespace NodePilot.Core.Clients;

/// <summary>
/// Thrown by the HTTP-only clients (the <c>np</c> CLI and the <c>nodepilot-mcp</c> server)
/// on every non-2xx response. Carries the HTTP status, so commands and tools can branch on
/// 401/403/404/409/423, plus the parsed <c>ProblemDetails</c> payload when the server sent
/// one. Shared by both clients, like <see cref="ClientSessionSecurity"/>, to keep them in step.
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

    /// <summary>True for 401: the caller must re-authenticate.</summary>
    public bool IsUnauthorized => StatusCode == HttpStatusCode.Unauthorized;

    /// <summary>True for 403: the caller is authenticated but lacks the required role.</summary>
    public bool IsForbidden => StatusCode == HttpStatusCode.Forbidden;

    /// <summary>True for 423: the workflow is checked out by another user.</summary>
    public bool IsLocked => (int)StatusCode == 423;

    /// <summary>True for 409: conflicting state, such as lock contention or idempotency.</summary>
    public bool IsConflict => StatusCode == HttpStatusCode.Conflict;

    /// <summary>True for 404: the resource does not exist.</summary>
    public bool IsNotFound => StatusCode == HttpStatusCode.NotFound;
}
