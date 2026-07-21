using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace NodePilot.Api.Controllers;

internal static class ApiProblems
{
    public static BadRequestObjectResult BadRequest(
        ControllerBase controller,
        string code,
        string detail,
        string title = "Invalid request")
    {
        var result = new BadRequestObjectResult(BuildProblem(controller, StatusCodes.Status400BadRequest, code, title, detail));
        result.ContentTypes.Add("application/problem+json");
        return result;
    }

    public static NotFoundObjectResult NotFound(
        ControllerBase controller,
        string code,
        string detail,
        string title = "Not found")
    {
        var result = new NotFoundObjectResult(BuildProblem(controller, StatusCodes.Status404NotFound, code, title, detail));
        result.ContentTypes.Add("application/problem+json");
        return result;
    }

    public static ConflictObjectResult Conflict(
        ControllerBase controller,
        string code,
        string detail,
        string title = "Conflict")
    {
        var result = new ConflictObjectResult(BuildProblem(controller, StatusCodes.Status409Conflict, code, title, detail));
        result.ContentTypes.Add("application/problem+json");
        return result;
    }

    public static UnauthorizedObjectResult Unauthorized(
        ControllerBase controller,
        string code,
        string detail,
        string title = "Unauthorized")
    {
        var result = new UnauthorizedObjectResult(BuildProblem(controller, StatusCodes.Status401Unauthorized, code, title, detail));
        result.ContentTypes.Add("application/problem+json");
        return result;
    }

    public static ProblemDetails BuildProblem(
        ControllerBase controller,
        int status,
        string code,
        string title,
        string detail)
        => BuildProblem(controller.ControllerContext.HttpContext, status, code, title, detail);

    public static ProblemDetails BuildProblem(
        HttpContext? httpContext,
        int status,
        string code,
        string title,
        string detail,
        IReadOnlyDictionary<string, object?>? extensions = null)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = httpContext?.Request?.Path.Value,
        };

        problem.Extensions["code"] = code;
        var traceId = httpContext?.TraceIdentifier ?? Activity.Current?.Id;
        if (!string.IsNullOrWhiteSpace(traceId))
            problem.Extensions["traceId"] = traceId;

        if (extensions is not null)
        {
            foreach (var (key, value) in extensions)
            {
                if (value is not null && !problem.Extensions.ContainsKey(key))
                    problem.Extensions[key] = value;
            }
        }

        return problem;
    }

    public static bool TryCreateFromLegacyPayload(
        HttpContext? httpContext,
        int status,
        object? payload,
        out ProblemDetails problem)
    {
        problem = default!;
        if (payload is null || payload is ProblemDetails)
            return false;
        if (payload is string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            problem = BuildProblem(httpContext, status, DefaultCode(status), DefaultTitle(status), text);
            return true;
        }

        var values = ReadPayloadProperties(payload);
        var detail = FirstString(values, "detail", "message", "error", "title");
        var code = FirstString(values, "code");
        if (string.IsNullOrWhiteSpace(detail))
        {
            if (string.IsNullOrWhiteSpace(code))
                return false;
            detail = code;
        }

        code ??= DefaultCode(status);

        var title = FirstString(values, "title") ?? DefaultTitle(status);
        var extensions = values
            .Where(kv => !IsProblemCoreProperty(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        problem = BuildProblem(httpContext, status, code, title, detail, extensions);
        return true;
    }

    private static Dictionary<string, object?> ReadPayloadProperties(object payload)
    {
        if (payload is IReadOnlyDictionary<string, object?> readOnly)
            return new Dictionary<string, object?>(readOnly, StringComparer.OrdinalIgnoreCase);
        if (payload is IDictionary<string, object?> dictionary)
            return new Dictionary<string, object?>(dictionary, StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in payload.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;
            result[property.Name] = property.GetValue(payload);
        }

        return result;
    }

    private static string? FirstString(IReadOnlyDictionary<string, object?> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value) && value is not null)
            {
                var s = value.ToString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
        }

        return null;
    }

    private static bool IsProblemCoreProperty(string key)
        => key.Equals("type", StringComparison.OrdinalIgnoreCase)
           || key.Equals("title", StringComparison.OrdinalIgnoreCase)
           || key.Equals("status", StringComparison.OrdinalIgnoreCase)
           || key.Equals("detail", StringComparison.OrdinalIgnoreCase)
           || key.Equals("instance", StringComparison.OrdinalIgnoreCase)
           || key.Equals("message", StringComparison.OrdinalIgnoreCase)
           || key.Equals("error", StringComparison.OrdinalIgnoreCase)
           || key.Equals("code", StringComparison.OrdinalIgnoreCase);

    private static string DefaultCode(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "BAD_REQUEST",
        StatusCodes.Status401Unauthorized => "UNAUTHORIZED",
        StatusCodes.Status403Forbidden => "FORBIDDEN",
        StatusCodes.Status404NotFound => "NOT_FOUND",
        StatusCodes.Status405MethodNotAllowed => "METHOD_NOT_ALLOWED",
        StatusCodes.Status409Conflict => "CONFLICT",
        StatusCodes.Status412PreconditionFailed => "PRECONDITION_FAILED",
        StatusCodes.Status423Locked => "LOCKED",
        StatusCodes.Status428PreconditionRequired => "PRECONDITION_REQUIRED",
        StatusCodes.Status500InternalServerError => "INTERNAL_SERVER_ERROR",
        StatusCodes.Status502BadGateway => "BAD_GATEWAY",
        StatusCodes.Status503ServiceUnavailable => "SERVICE_UNAVAILABLE",
        _ => $"HTTP_{status}",
    };

    private static string DefaultTitle(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "Invalid request",
        StatusCodes.Status401Unauthorized => "Unauthorized",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "Not found",
        StatusCodes.Status405MethodNotAllowed => "Method not allowed",
        StatusCodes.Status409Conflict => "Conflict",
        StatusCodes.Status412PreconditionFailed => "Precondition failed",
        StatusCodes.Status423Locked => "Locked",
        StatusCodes.Status428PreconditionRequired => "Precondition required",
        StatusCodes.Status500InternalServerError => "Internal server error",
        StatusCodes.Status502BadGateway => "Bad gateway",
        StatusCodes.Status503ServiceUnavailable => "Service unavailable",
        _ => "HTTP error",
    };
}
