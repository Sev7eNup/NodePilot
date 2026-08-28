using System.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace NodePilot.Api.Hosting;

/// <summary>
/// Polls database connectivity until the server accepts connections or a timeout elapses, so
/// the API waits for the database instead of crashing on it when <c>MigrationBootstrapper</c>
/// runs <c>Database.Migrate()</c>.
/// <para>
/// Runs in <b>both</b> deployment modes. Desktop can start before its bundled PostgreSQL service;
/// Server can start while a remote SQL Server or PostgreSQL instance is still recovering.
/// </para>
/// <para>
/// Only <b>connectivity</b> is retried. A schema or migration failure is a deterministic bug,
/// not a transient startup race, so it is never retried here — the gate returns and the caller
/// proceeds straight into migration, which surfaces such errors immediately.
/// </para>
/// </summary>
public static class DatabaseReadinessGate
{
    /// <summary>How long boot waits for the database to accept connections.</summary>
    public const string StartupWaitSecondsKey = "Database:StartupWaitSeconds";

    /// <summary>Applies when the key is absent, empty or unparseable.</summary>
    public static readonly TimeSpan DefaultStartupWait = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Upper bound. Past this point an unreachable database is an operational problem, not a
    /// startup race, and silently hanging service start is the worst way to report it. Also
    /// contains the "thought the unit was something else" typo — 86400 would hang boot for a day.
    /// </summary>
    public static readonly TimeSpan MaxStartupWait = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Reads <see cref="StartupWaitSecondsKey"/>. Absent, empty, or unparseable values use
    /// <see cref="DefaultStartupWait"/>; zero or negative values return <see cref="TimeSpan.Zero"/>
    /// (probe once, then proceed, which is the documented opt-out); anything above
    /// <see cref="MaxStartupWait"/> is clamped to it. Never throws.
    /// </summary>
    public static TimeSpan ResolveStartupWait(IConfiguration configuration)
    {
        var raw = configuration[StartupWaitSecondsKey];
        if (string.IsNullOrWhiteSpace(raw)) return DefaultStartupWait;
        if (!int.TryParse(raw.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            return DefaultStartupWait;
        if (seconds <= 0) return TimeSpan.Zero;

        var requested = TimeSpan.FromSeconds(seconds);
        return requested > MaxStartupWait ? MaxStartupWait : requested;
    }

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
