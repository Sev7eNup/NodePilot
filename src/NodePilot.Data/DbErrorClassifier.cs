using System.IO;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace NodePilot.Data;

/// <summary>
/// The condition a failed database operation represents, in precedence order.
///
/// <para>One exception can carry several of these signals at once, so declaration order decides
/// which one wins: the lower enum value is kept.</para>
/// </summary>
public enum DbFailureKind
{
    /// <summary>Not a database-availability signal at all (a unique violation, a bug, a cancelled caller).</summary>
    None = 0,

    /// <summary>
    /// The server is healthy and answering; it is out of some resource right now (connection slots,
    /// memory grants, a lock). First in precedence because these are simultaneously
    /// connection-failure-shaped and *proof the server is alive* — misreading one as an outage would
    /// take a whole installation down the moment it got busy.
    /// </summary>
    CapacityBackpressure,

    /// <summary>
    /// The server answered and declined: bad password, missing database, failed TLS verification.
    /// Not transient — waiting will never fix it, so it must be visible rather than hidden behind a
    /// cheerful "reconnecting…".
    /// </summary>
    ConnectionRejected,

    /// <summary>The statement did not finish in time. The server is reachable and the SQL is valid.</summary>
    CommandTimeout,

    /// <summary>Nothing is listening, or the transport died mid-conversation.</summary>
    ConnectionFailure,
}

/// <summary>
/// Maps provider-specific database exceptions onto the handful of conditions the application
/// actually branches on.
///
/// <para>The alternative — substring-matching the exception message — breaks in two directions
/// that are both hard to notice: it depends on the server's message locale (a German SQL Server
/// says "Verletzung der UNIQUE KEY-Einschränkung"), and a broad term like "duplicate" matches
/// unrelated errors. Every provider reports these conditions as a stable, documented code, so
/// that is what gets checked.</para>
///
/// <para><b>Why one ordered classifier instead of four independent predicates.</b> Measured against
/// Npgsql 10.0.3 on 2026-08-06: a *connect* timeout arrives as
/// <c>NpgsqlException("The operation has timed out") → TimeoutException</c>, and a *command* timeout on
/// an already-open pooled connection arrives as
/// <c>NpgsqlException("Exception while reading from stream") → TimeoutException</c>. The two conditions
/// are therefore **indistinguishable by exception shape** — no predicate reading only the exception can
/// separate "the server is gone" from "this one query was slow". Independent predicates would each
/// answer <c>true</c> and the caller's evaluation order would silently decide the outcome. Here the
/// order is declared once, in <see cref="DbFailureKind"/>, and the ambiguity is resolved by *context*
/// instead: see <see cref="ClassifyConnectionFailure"/>.</para>
/// </summary>
public static class DbErrorClassifier
{
    // PostgreSQL: 23505 unique_violation (SQLSTATE, class 23 = integrity constraint violation).
    private const string PostgresUniqueViolation = "23505";

    // SQL Server: 2627 = unique constraint, 2601 = unique index. Both mean "duplicate key".
    private const int SqlServerUniqueConstraint = 2627;
    private const int SqlServerUniqueIndex = 2601;

    // SQLite: primary result code 19 (SQLITE_CONSTRAINT); extended 2067 (CONSTRAINT_UNIQUE) and
    // 1555 (CONSTRAINT_PRIMARYKEY). The test suite runs on SQLite, so this branch is what keeps
    // the classifier honest in CI.
    private const int SqliteConstraint = 19;
    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintPrimaryKey = 1555;

    private const string SqlServerExceptionTypeName = "Microsoft.Data.SqlClient.SqlException";

    /// <summary>
    /// True when the update failed because it would have duplicated a unique key — on any of
    /// the three providers this codebase runs against.
    /// </summary>
    public static bool IsUniqueConstraintViolation(DbUpdateException exception)
        => exception.InnerException is not null && IsUniqueConstraintViolation(exception.InnerException);

    /// <summary>Same check against an already-unwrapped provider exception.</summary>
    public static bool IsUniqueConstraintViolation(Exception exception) => exception switch
    {
        PostgresException pg => pg.SqlState == PostgresUniqueViolation,
        SqliteException sqlite => sqlite.SqliteExtendedErrorCode is SqliteConstraintUnique or SqliteConstraintPrimaryKey
                               || sqlite.SqliteErrorCode == SqliteConstraint,
        _ => IsSqlServerUniqueViolation(exception),
    };

    /// <summary>
    /// SQL Server is matched by type name rather than by a typed pattern so this class does not
    /// have to reference Microsoft.Data.SqlClient directly; the error number lives on the
    /// exception's <c>Number</c> property either way.
    /// </summary>
    private static bool IsSqlServerUniqueViolation(Exception exception)
    {
        var number = SqlServerNumber(exception);
        return number is SqlServerUniqueConstraint or SqlServerUniqueIndex;
    }

    private static int? SqlServerNumber(Exception exception)
    {
        if (exception.GetType().FullName != SqlServerExceptionTypeName) return null;
        return exception.GetType().GetProperty("Number")?.GetValue(exception) as int?;
    }

    // ---------------------------------------------------------------------------------------------
    // Availability classification
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Classifies a failure by walking the whole <see cref="Exception.InnerException"/> chain and
    /// keeping the highest-precedence signal found anywhere in it.
    ///
    /// <para>The chain is walked rather than only inspected at the top because EF Core wraps provider
    /// exceptions, and its retrying execution strategy wraps them again after the last attempt — a
    /// failure that started life as a <c>SocketException</c> can arrive three layers deep inside a
    /// <c>RetryLimitExceededException</c>.</para>
    ///
    /// <para>This is the classifier for the <b>command</b> path and for the exception handlers. The
    /// connection path uses <see cref="ClassifyConnectionFailure"/>, which resolves the shape ambiguity
    /// documented on this class.</para>
    /// </summary>
    public static DbFailureKind Classify(Exception? exception)
    {
        var best = DbFailureKind.None;

        for (var current = exception; current is not null; current = current.InnerException)
        {
            var kind = ClassifyOne(current);
            if (kind is DbFailureKind.None) continue;

            // Lower enum value wins; see the DbFailureKind doc comment.
            if (best is DbFailureKind.None || kind < best) best = kind;

            // Nothing outranks capacity backpressure, so the rest of the chain cannot change the answer.
            if (best is DbFailureKind.CapacityBackpressure) break;
        }

        return best;
    }

    /// <summary>
    /// Classifies a failure that EF reported through the <b>connection</b> interceptor hook — i.e. a
    /// physical open that did not succeed.
    ///
    /// <para><b>Context beats shape.</b> A failed physical open is the one genuinely negative liveness
    /// signal in the system, and it stays that even when the exception's shape says "timeout": a
    /// connect timeout means the server did not complete a handshake, which is a dead server, not a
    /// slow query. Since Npgsql reports both conditions with an identical exception shape (measured;
    /// see the class remarks), the hook the failure arrived on is the only information that can tell
    /// them apart — so it is what decides.</para>
    ///
    /// <para>The two answers that are *not* folded are the two that remain true regardless of which
    /// hook reported them: the server answered and declined, or the server is alive and out of
    /// resources. Both would be actively wrong to report as an outage.</para>
    /// </summary>
    public static DbFailureKind ClassifyConnectionFailure(Exception? exception) => Classify(exception) switch
    {
        DbFailureKind.CapacityBackpressure => DbFailureKind.CapacityBackpressure,
        DbFailureKind.ConnectionRejected => DbFailureKind.ConnectionRejected,
        DbFailureKind.CommandTimeout or DbFailureKind.ConnectionFailure => DbFailureKind.ConnectionFailure,
        // Provider-specific InvalidOperationException shapes are not proof that the server is gone.
        // Leave an unknown open failure undecided so the connection interceptor can arm the dedicated
        // SELECT-1 probe rather than taking the entire installation down on an unfamiliar client error.
        _ => DbFailureKind.None,
    };

    /// <summary>
    /// True when the command did not finish in time — the database is reachable and the statement
    /// is valid, it simply took longer than the client was willing to wait.
    ///
    /// <para>This is worth distinguishing from every other database failure because the honest
    /// answer to the user is different: not "something went wrong" but "the database is too busy
    /// right now, try again". It also has nothing to do with the query being badly written; the
    /// case that prompted this classifier spent 99.8% of its time waiting for a 22 MB memory
    /// grant on a memory-starved server.</para>
    /// </summary>
    public static bool IsCommandTimeout(Exception? exception) => Classify(exception) is DbFailureKind.CommandTimeout;

    private static DbFailureKind ClassifyOne(Exception exception)
    {
        // Pool exhaustion is checked before anything else about the exception, because both providers
        // report it with a type that would otherwise fall straight into ConnectionFailure — and it is
        // the one "connection could not be obtained" that proves the server is fine.
        if (IsPoolExhaustion(exception)) return DbFailureKind.CapacityBackpressure;

        switch (exception)
        {
            case PostgresException pg:
                return ClassifyBySqlState(pg.SqlState);

            // SQLite is a test backend only (see CLAUDE.md "Datenbank"). It deliberately has no
            // availability branch: a SQLITE_BUSY from an in-memory test fixture must never be able to
            // trip a production-shaped breaker.
            case SqliteException:
                return DbFailureKind.None;

            case TimeoutException:
                return DbFailureKind.CommandTimeout;

            // EndOfStreamException derives from IOException, so this covers "the peer closed the socket
            // mid-conversation" as well as a raw transport error.
            case SocketException:
            case IOException:
                return DbFailureKind.ConnectionFailure;

            case NpgsqlException:
                // A non-PostgresException NpgsqlException is a transport-level failure. When it wraps a
                // TimeoutException the chain walk in Classify() promotes the result to CommandTimeout,
                // because CommandTimeout outranks ConnectionFailure.
                return DbFailureKind.ConnectionFailure;
        }

        var number = SqlServerNumber(exception);
        return number is null ? DbFailureKind.None : ClassifyBySqlServerNumber(number.Value);
    }

    private static bool IsPoolExhaustion(Exception exception)
    {
        // Npgsql: NpgsqlException("The connection pool has been exhausted...").
        // SqlClient: InvalidOperationException("Timeout expired. The timeout period elapsed prior to
        // obtaining a connection from the pool...") — note this one is NOT a SqlException, so it
        // carries no Number and cannot be table-matched.
        if (exception is not (NpgsqlException or InvalidOperationException)) return false;
        var message = exception.Message;
        return message.Contains("pool has been exhausted", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection from the pool", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// PostgreSQL SQLSTATE table. Internal so the tests can drive it directly — <c>PostgresException</c>
    /// is constructible but noisy, and the table is the part that carries the risk.
    /// </summary>
    internal static DbFailureKind ClassifyBySqlState(string? sqlState) => sqlState switch
    {
        // Class 53 — insufficient resources. 55P03 is lock_not_available.
        "53000" or "53100" or "53200" or "53300" or "53400" or "55P03" => DbFailureKind.CapacityBackpressure,

        // The server spoke and said no in a way ONLY an operator can fix: authentication, pg_hba
        // rejection, missing database. ConnectionRejected drives the probe's "configuration problem,
        // retrying will not fix it" ERROR and the banner's escalation copy, so the bar for membership
        // is "waiting can never help" — a state the server leaves BY ITSELF does not qualify.
        "08004" or "28000" or "28P01" or "3D000" => DbFailureKind.ConnectionRejected,

        // query_canceled — what statement_timeout produces server-side.
        "57014" => DbFailureKind.CommandTimeout,

        // Class 08 — connection exception, PLUS the class-57 shutdown/startup states: admin_shutdown,
        // crash_shutdown, cannot_connect_now (a server in crash recovery answers exactly this while it
        // replays WAL) and database_dropped. All of them clear on their own once the server is back,
        // which is the definition of "unreachable, keep probing" — classifying them as rejections made
        // the probe tell the operator "configuration problem, retrying will not fix it" during every
        // ROUTINE restart, which is the reverse of the truth.
        "08000" or "08001" or "08003" or "08006" or "08007" or "08P01"
            or "57P01" or "57P02" or "57P03" or "57P04" => DbFailureKind.ConnectionFailure,

        // Deliberately None:
        //   57P05 idle_session_timeout — a healthy server retiring an idle connection. Classifying it
        //         would open the breaker every time the server does routine housekeeping.
        //   40001 serialization_failure / 40P01 deadlock_detected — contention, already the retrying
        //         execution strategy's job, and both prove the server is alive and working.
        //   23505 unique_violation — see IsUniqueConstraintViolation.
        _ => DbFailureKind.None,
    };

    /// <summary>
    /// SQL Server error-number table. Internal for the same reason as <see cref="ClassifyBySqlState"/>,
    /// and additionally because <c>SqlException</c> has no public constructor — the table is testable
    /// even though a synthetic <c>SqlException</c> is not.
    /// </summary>
    internal static DbFailureKind ClassifyBySqlServerNumber(int number) => number switch
    {
        // Out of memory / out of locks / lock request timeout / no query-execution memory /
        // database full / Azure SQL throttling and session limits.
        701 or 1105 or 1204 or 1222 or 8645 or 9002 or 10928 or 10929 or 40501 or 49918 or 49919 or 49920
            => DbFailureKind.CapacityBackpressure,

        // Operator-fixable rejections only: cannot open database / database unusable, login failed
        // (session + Windows), suspect database. Same membership bar as the Postgres table above.
        926 or 4060 or 4064 or 18456 => DbFailureKind.ConnectionRejected,

        // -2 is the client giving up: it covers both a command timeout and a connect timeout, which is
        // exactly the ambiguity ClassifyConnectionFailure resolves by context.
        -2 => DbFailureKind.CommandTimeout,

        // Transport (general network error, server not found, reset/aborted/refused, pre-login
        // handshake, semaphore timeout, host not found) PLUS the self-clearing server states: database
        // in recovery/restoring (927/941/945), server shutting down or in the middle of starting up
        // (6005/6006), and the Azure transient pair 40197/40613, which Microsoft's own guidance says
        // to retry. They land here rather than in ConnectionRejected so a routine restart or failover
        // reads as "unreachable, keep probing" instead of "configuration problem".
        -1 or 2 or 20 or 53 or 64 or 121 or 233 or 258 or 10053 or 10054 or 10060 or 10061 or 11001
            or 927 or 941 or 945 or 6005 or 6006 or 18401 or 40197 or 40613
            => DbFailureKind.ConnectionFailure,

        // 1205 (deadlock victim) is deliberately absent for the same reason as Postgres 40P01.
        _ => DbFailureKind.None,
    };
}
