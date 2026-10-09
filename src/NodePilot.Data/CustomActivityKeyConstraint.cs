using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace NodePilot.Data;

/// <summary>The live custom-key invariant shared by schema, writes and migration diagnostics.</summary>
public static class CustomActivityKeyConstraint
{
    public const string IndexName = "UX_CustomActivityDefinitions_LiveKey";

    public static string Filter(string? providerName) =>
        providerName?.Contains("Npgsql", StringComparison.Ordinal) == true
            ? "\"IsDeleted\" = FALSE" : "\"IsDeleted\" = 0";

    public static bool IsViolation(Exception error)
    {
        if (error is DbUpdateException { InnerException: { } inner }) return IsViolation(inner);
        if (error is PostgresException pg)
            return pg.SqlState == "23505" && pg.ConstraintName == IndexName;
        if (error is SqliteException sqlite)
            return sqlite.SqliteExtendedErrorCode == 2067
                && sqlite.Message.Contains("UNIQUE constraint failed: CustomActivityDefinitions.Key", StringComparison.Ordinal);
        if (error.GetType().FullName == "Microsoft.Data.SqlClient.SqlException")
        {
            var number = error.GetType().GetProperty("Number")?.GetValue(error) as int?;
            return number is 1505 or 2601 or 2627
                && error.Message.Contains(IndexName, StringComparison.Ordinal);
        }
        return false;
    }
}
