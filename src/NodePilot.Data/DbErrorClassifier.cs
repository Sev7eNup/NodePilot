using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace NodePilot.Data;

/// <summary>
/// Maps provider-specific database exceptions onto the handful of conditions the application
/// actually branches on.
///
/// <para>The alternative — substring-matching the exception message — breaks in two directions
/// that are both hard to notice: it depends on the server's message locale (a German SQL Server
/// says "Verletzung der UNIQUE KEY-Einschränkung"), and a broad term like "duplicate" matches
/// unrelated errors. Every provider reports these conditions as a stable, documented code, so
/// that is what gets checked.</para>
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

    // SQL Server reports a client-side command timeout as error -2. It is not a server error at
    // all: the client gave up and cancelled. Observed in the field as a workflow-list query that
    // needed 85 ms of CPU and 2,585 logical reads but waited 55 seconds on RESOURCE_SEMAPHORE -
    // a 22 MB memory grant it could not get while the box was under load.
    private const int SqlServerCommandTimeout = -2;

    // PostgreSQL: statement_timeout cancels the query server-side and reports 57014
    // (query_canceled). Npgsql surfaces a client-side timeout as a plain TimeoutException instead,
    // which the caller below also covers.
    private const string PostgresQueryCanceled = "57014";

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
        if (exception.GetType().FullName != "Microsoft.Data.SqlClient.SqlException") return false;
        var number = exception.GetType().GetProperty("Number")?.GetValue(exception) as int?;
        return number is SqlServerUniqueConstraint or SqlServerUniqueIndex;
    }

    /// <summary>
    /// True when the command did not finish in time — the database is reachable and the statement
    /// is valid, it simply took longer than the client was willing to wait.
    ///
    /// <para>This is worth distinguishing from every other database failure because the honest
    /// answer to the user is different: not "something went wrong" but "the database is too busy
    /// right now, try again". It also has nothing to do with the query being badly written; the
    /// case that prompted this classifier spent 99.8% of its time waiting for a 22 MB memory
    /// grant on a memory-starved server.</para>
    ///
    /// <para>The exception is unwrapped along <c>InnerException</c> because EF Core wraps provider
    /// exceptions, and its retrying execution strategy wraps them again after the last attempt.</para>
    /// </summary>
    public static bool IsCommandTimeout(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException) return true;
            if (current is PostgresException pg && pg.SqlState == PostgresQueryCanceled) return true;
            if (IsSqlServerCommandTimeout(current)) return true;
        }
        return false;
    }

    private static bool IsSqlServerCommandTimeout(Exception exception)
    {
        if (exception.GetType().FullName != "Microsoft.Data.SqlClient.SqlException") return false;
        var number = exception.GetType().GetProperty("Number")?.GetValue(exception) as int?;
        return number == SqlServerCommandTimeout;
    }
}
