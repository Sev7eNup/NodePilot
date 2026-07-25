using System.Diagnostics;

namespace NodePilot.Api.Hosting;

/// <summary>
/// Polls database connectivity until the server accepts connections or a timeout elapses.
/// Used in <c>Deployment:Mode=Desktop</c> so the API waits for the bundled Postgres Windows
/// service to finish starting before <c>MigrationBootstrapper</c> runs <c>Database.Migrate()</c>.
/// <para>
/// Only <b>connectivity</b> is retried. A schema or migration failure is a deterministic bug,
/// not a transient startup race, so it is never retried here — the gate returns and the caller
/// proceeds straight into migration, which surfaces such errors immediately.
/// </para>
/// </summary>
public static class DatabaseReadinessGate
{
    /// <summary>
    /// Repeatedly invokes <paramref name="canConnectAsync"/> until it returns true or
    /// <paramref name="timeout"/> elapses, sleeping <paramref name="pollInterval"/> between
    /// attempts. Probe exceptions are treated as "not ready yet" (the server socket may not be
    /// listening). Returns true once connectable, false on timeout. <c>delayAsync</c> is an
    /// injectable delay (defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>) so
    /// tests run instantly.
    /// </summary>
    public static async Task<bool> WaitForDatabaseAsync(
        Func<CancellationToken, Task<bool>> canConnectAsync,
        TimeSpan timeout,
        TimeSpan pollInterval,
        ILogger logger,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        CancellationToken ct = default)
    {
        delayAsync ??= Task.Delay;
        var stopwatch = Stopwatch.StartNew();
        var attempt = 0;

        while (true)
        {
            attempt++;
            try
            {
                if (await canConnectAsync(ct).ConfigureAwait(false))
                {
                    if (attempt > 1)
                        logger.LogInformation(
                            "Database reachable after {ElapsedSeconds:n1}s ({Attempts} attempts).",
                            stopwatch.Elapsed.TotalSeconds, attempt);
                    return true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex,
                    "Database connectivity probe failed (attempt {Attempt}); will retry until timeout.",
                    attempt);
            }

            if (stopwatch.Elapsed >= timeout)
            {
                logger.LogError(
                    "Database not reachable after {TimeoutSeconds:n0}s ({Attempts} attempts). " +
                    "Proceeding to migration bootstrap, which will surface the underlying connection error.",
                    timeout.TotalSeconds, attempt);
                return false;
            }

            logger.LogInformation(
                "Waiting for the database to accept connections ({ElapsedSeconds:n0}/{TimeoutSeconds:n0}s)...",
                stopwatch.Elapsed.TotalSeconds, timeout.TotalSeconds);
            await delayAsync(pollInterval, ct).ConfigureAwait(false);
        }
    }
}
