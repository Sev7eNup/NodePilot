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
}
