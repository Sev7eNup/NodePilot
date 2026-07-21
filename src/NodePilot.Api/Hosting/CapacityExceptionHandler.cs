using Microsoft.AspNetCore.Diagnostics;
using NodePilot.Core.Exceptions;

namespace NodePilot.Api.Hosting;

/// <summary>
/// Maps <see cref="ExecutionCapacityException"/> to a 503 Service Unavailable + Retry-After
/// response, instead of letting it leak out as an anonymous 500 (audit finding H-3). The engine
/// throws this exception when the global or per-user execution capacity limit is exhausted; the
/// HTTP response gives the client a clear retry hint instead of a misleading generic error.
/// Only applies to synchronous paths that run through the ASP.NET pipeline; background
/// dispatch paths (webhook, external trigger) log the exception in their own catch block instead.
/// </summary>
public sealed class CapacityExceptionHandler : IExceptionHandler
{
    private const int RetryAfterSeconds = 30;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ExecutionCapacityException capEx) return false;
        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers["Retry-After"] = RetryAfterSeconds.ToString();
        await httpContext.Response.WriteAsJsonAsync(
            new { message = capEx.Message }, cancellationToken);
        return true;
    }
}
