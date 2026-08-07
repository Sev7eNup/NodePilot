using Microsoft.AspNetCore.Diagnostics;
using NodePilot.Data;
using NodePilot.Data.Availability;

namespace NodePilot.Api.Hosting;

/// <summary>
/// Backstop for a database failure that escaped before the breaker flipped — the in-flight requests
/// that were already past <see cref="DatabaseAvailabilityMiddleware"/> when the server went away.
///
/// <para><b>Why it reads the breaker rather than the exception alone.</b> A connect timeout and a
/// command timeout arrive in the identical exception shape (measured; see
/// <see cref="DbErrorClassifier"/>), so "is the database gone" cannot be answered from the exception.
/// The breaker can only have been opened by a connection-class event, so consulting it means a slow
/// query can never synthesise an "unavailable" answer no matter how its exception is shaped.</para>
///
/// <para><b>Why it also requires a database-shaped exception.</b> Reading only the breaker would rewrite
/// every <c>NullReferenceException</c> raised anywhere in the process during an open window into "the
/// database is down" — hiding real regressions for the entire duration of an outage, precisely when an
/// operator is reading the logs.</para>
/// </summary>
public sealed class DatabaseUnavailableExceptionHandler(
    IDatabaseAvailability availability,
    ILogger<DatabaseUnavailableExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (availability.IsServable) return false;
        if (DbErrorClassifier.Classify(exception) is DbFailureKind.None) return false;

        // Warning, not error: the service is doing exactly what it should. The breaker already logged
        // the outage once; this line exists to attribute a specific failed request to it.
        logger.LogWarning(
            "Database unavailable while serving {Method} {Path}; returning 503 {Code}.",
            httpContext.Request.Method, httpContext.Request.Path, DatabaseUnavailableResponse.UnavailableCode);

        await DatabaseUnavailableResponse.WriteUnavailableAsync(
            httpContext, availability.CurrentOutage, cancellationToken);
        return true;
    }
}
