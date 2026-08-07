using NodePilot.Data.Availability;

namespace NodePilot.Api.Hosting;

/// <summary>
/// The single owner of the 503 body that speaks for the database.
///
/// <para>Three places need to produce it — the availability middleware (which short-circuits before any
/// controller), <see cref="DatabaseUnavailableExceptionHandler"/> and
/// <see cref="DatabaseTimeoutExceptionHandler"/> — and they must agree, because the SPA branches on
/// <c>code</c> to decide whether to raise the outage banner. Three hand-written bodies would drift.</para>
///
/// <para><b>Not ProblemDetails.</b> ADR 0007's error contract is applied by an MVC result filter, and a
/// middleware short-circuit never reaches MVC. Rather than have the same condition answer in two
/// different shapes depending on where it was caught, both use this one.</para>
/// </summary>
public static class DatabaseUnavailableResponse
{
    /// <summary>The breaker is open: the database is not answering at all.</summary>
    public const string UnavailableCode = "DATABASE_UNAVAILABLE";

    /// <summary>The database answered too slowly, but nothing is known to be broken.</summary>
    public const string TimeoutCode = "DATABASE_TIMEOUT";

    /// <summary>
    /// Deliberately coarser than the probe's 5 s outage cadence: the probe must notice recovery quickly,
    /// while callers retrying every probe tick would produce 120 pointless requests per tab in ten minutes.
    /// </summary>
    public const int UnavailableRetryAfterSeconds = 15;

    /// <summary>Deliberately short: the condition that causes a timeout clears in seconds, not minutes.</summary>
    public const int TimeoutRetryAfterSeconds = 5;

    public static Task WriteUnavailableAsync(
        HttpContext context, DatabaseOutage? outage, CancellationToken cancellationToken = default)
    {
        // A wrong password or missing database is not an outage that clears on its own — promising
        // "resumes automatically" over one would be a lie that hides a configuration problem behind a
        // cheerful banner. The reason and the honest retryable flag let the SPA (and any external
        // caller) tell the two situations apart without parsing prose.
        var rejected = outage?.Reason is DatabaseOutageReason.RejectedByServer;
        return WriteAsync(
            context,
            UnavailableCode,
            rejected
                ? "The database rejected the connection (wrong credentials, missing database or failed "
                  + "TLS verification). This needs an administrator; retrying alone will not fix it."
                : "The database is not reachable right now. NodePilot keeps checking and resumes on "
                  + "its own as soon as it answers.",
            UnavailableRetryAfterSeconds,
            outage?.Reason.ToString(),
            retryable: !rejected,
            cancellationToken);
    }

    public static Task WriteTimeoutAsync(HttpContext context, CancellationToken cancellationToken = default)
        => WriteAsync(
            context,
            TimeoutCode,
            "The database did not answer in time. It is most likely under load - please try again in a moment.",
            TimeoutRetryAfterSeconds,
            reason: null,
            retryable: true,
            cancellationToken);

    private static async Task WriteAsync(
        HttpContext context, string code, string message, int retryAfterSeconds,
        string? reason, bool retryable, CancellationToken cancellationToken)
    {
        // A short-circuit can race a response that has already started (e.g. a streaming endpoint).
        // Writing a second set of headers would throw and turn a clean 503 into a connection reset.
        if (context.Response.HasStarted) return;

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers["Retry-After"] = retryAfterSeconds.ToString();
        await context.Response.WriteAsJsonAsync(
            new { code, message, retryAfterSeconds, reason, retryable }, cancellationToken);
    }
}

/// <summary>
/// What <c>/healthz/database</c> answers. Deliberately thin: this endpoint is anonymous, so it must not
/// disclose the provider, the host, error numbers or attempt counts. The SPA computes elapsed time from
/// <see cref="SinceUtc"/> itself.
/// </summary>
/// <param name="Status">One of <c>ok</c>, <c>armed</c>, <c>unavailable</c>.</param>
/// <param name="SinceUtc">When the outage began; <c>null</c> unless the breaker is open.</param>
/// <param name="Reason">Why, coarsely. <c>null</c> unless the breaker is open.</param>
public sealed record DatabaseHealthDto(string Status, DateTime? SinceUtc, string? Reason);

/// <summary>
/// Pure function behind <c>/healthz/database</c>, in the shape of <c>ClusterSetup.ComputeLeaderHealth</c>
/// so it is testable without a host.
///
/// <para><b>It answers 200 in every state, including an outage.</b> That is not an oversight. A 503 here
/// would be indistinguishable from "the process is gone" to the SPA polling it, which is exactly the
/// misleading-indicator bug this whole feature exists to fix — <c>/healthz/live</c> staying green while
/// the product was dead. <c>/healthz/ready</c> keeps the 503 convention for load balancers and
/// orchestrators; this endpoint reports rather than gates.</para>
/// </summary>
public static class DatabaseHealthEndpoint
{
    public static DatabaseHealthDto Compute(IDatabaseAvailability availability)
    {
        var outage = availability.CurrentOutage;
        if (outage is not null)
            return new DatabaseHealthDto("unavailable", outage.SinceUtc, outage.Reason.ToString());

        return availability.State switch
        {
            DatabaseAvailabilityState.Armed => new DatabaseHealthDto("armed", null, null),
            _ => new DatabaseHealthDto("ok", null, null),
        };
    }
}
