using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>
    /// Applies when the key is absent, empty or unparseable. Long enough for a SQL Server on the same
    /// host, which Windows starts delayed-automatic about two minutes after boot.
    /// </summary>
    public static readonly TimeSpan DefaultStartupWait = TimeSpan.FromSeconds(300);

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
    /// Opens and closes one connection. Unlike <c>CanConnectAsync</c>, which returns false and
    /// discards the cause, a failure throws, so the wait loop can say why the database refused.
    /// Uses the raw ADO.NET connection: EF would log every refused attempt as an error with a
    /// stack trace, every poll interval, for as long as the database is down.
    /// </summary>
    public static async Task<bool> OpenAndCloseAsync(DbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        // A connection handed to the context already open (in-memory SQLite) is reachable by definition.
        if (connection.State == System.Data.ConnectionState.Open) return true;
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await connection.CloseAsync().ConfigureAwait(false);
        return true;
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
        var reason = "the server did not accept the connection";

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
                reason = DescribeFailure(ex);
                logger.LogDebug(ex,
                    "Database connectivity probe failed (attempt {Attempt}); will retry until timeout.",
                    attempt);
            }

            if (stopwatch.Elapsed >= timeout)
            {
                logger.LogError(
                    "Database not reachable after {TimeoutSeconds:n0}s ({Attempts} attempts): {Reason}. " +
                    "Proceeding to migration bootstrap, which will surface the underlying connection error.",
                    timeout.TotalSeconds, attempt, reason);
                return false;
            }

            logger.LogInformation(
                "Waiting for the database to accept connections ({ElapsedSeconds:n0}/{TimeoutSeconds:n0}s): {Reason}",
                stopwatch.Elapsed.TotalSeconds, timeout.TotalSeconds, reason);
            await delayAsync(pollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The outer message plus the innermost one when they differ: a provider wraps a TLS or login
    /// failure, and the inner message is the one that names the cause.
    /// </summary>
    internal static string DescribeFailure(Exception ex)
    {
        var inner = ex.GetBaseException();
        return ReferenceEquals(inner, ex) || inner.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({inner.Message})";
    }
}
