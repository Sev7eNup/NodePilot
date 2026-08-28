using Microsoft.AspNetCore.Diagnostics;
using NodePilot.Data;

namespace NodePilot.Api.Hosting;

/// <summary>
/// Maps a database command timeout to a 503 Service Unavailable with a stable error code and a
/// Retry-After hint, instead of letting it fall through to an anonymous 500.
///
/// <para>Found in the field: the workflow list query timed out after 120 seconds, the exception
/// escaped unhandled, and the SPA — which never read the query's error state — rendered "no
/// workflows exist". A busy database therefore looked exactly like an empty installation, on a
/// page showing 70 workflows in its counter. An anonymous 500 would have been better; this is
/// better still, because a timeout is genuinely a different answer from "something went wrong":
/// nothing is broken and retrying is the right move.</para>
///
/// <para>503 rather than 500 because the condition is transient and load-related, and rather than
/// 504 because no upstream gateway is involved — the database is a dependency of this service,
/// not a proxy in front of it.</para>
/// </summary>
public sealed class DatabaseTimeoutExceptionHandler : IExceptionHandler
{
    /// <summary>Stable, machine-readable, SCREAMING_SNAKE_CASE - the convention from ADR
    /// 0007.</summary>
    public const string ErrorCode = DatabaseUnavailableResponse.TimeoutCode;

    private readonly ILogger<DatabaseTimeoutExceptionHandler> _logger;

    public DatabaseTimeoutExceptionHandler(ILogger<DatabaseTimeoutExceptionHandler> logger)
        => _logger = logger;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // PostgreSQL 53300 and SQL Server 10928 indicate a live server without capacity.
        // Treat capacity backpressure like a command timeout so clients can retry.
        if (DbErrorClassifier.Classify(exception)
            is not (DbFailureKind.CommandTimeout or DbFailureKind.CapacityBackpressure)) return false;

        // Logged as a warning, not an error: the service is healthy, the database is busy. Logging
        // it at error level would train operators to ignore the level.
        _logger.LogWarning(
            exception,
            "Database command timed out serving {Method} {Path}; returning 503 {Code}.",
            httpContext.Request.Method, httpContext.Request.Path, ErrorCode);

        // One writer for both database 503s, so the SPA branches on `code` once instead of twice.
        await DatabaseUnavailableResponse.WriteTimeoutAsync(httpContext, cancellationToken);
        return true;
    }
}
