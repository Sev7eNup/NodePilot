using NodePilot.Core.Security;

namespace NodePilot.Api.Services.DbAdmin;

/// <summary>
/// Provider-neutral, fail-closed lexical guard applied at the executor boundary. This is deliberately
/// below every controller/tool adapter so callers cannot bypass the read policy by reusing the executor
/// directly. The database read-only transaction remains the second line of defence.
/// </summary>
internal static class DbAdminReadOnlySqlGuard
{
    /// <summary>Pseudo-token emitted by the shared inspector for PostgreSQL's <c>::</c> cast operator.</summary>
    public const string CastOperator = "::";

    /// <summary>
    /// Constructs that can collapse a complete row into one innocently named result column, which
    /// would otherwise carry a secret past the two name-based layers. Scoped to the tables that
    /// actually hold a protected column — see <see cref="DbAdminSecretColumns"/>.
    /// </summary>
    private static readonly HashSet<string> WholeRowProjectionIdentifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "to_json", "row_to_json", "to_jsonb", "json_agg", "jsonb_agg",
        "json_build_object", "jsonb_build_object", "json_object", "jsonb_object",
        "row_to_xml", "table_to_xml", "query_to_xml", "hstore",
        CastOperator,
    };

    private static readonly HashSet<string> DangerousKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        // REPLACE is deliberately absent: it is a standard string function on every supported
        // backend. The unsupported MySQL REPLACE INTO form is still stopped by INTO.
        "INSERT", "UPDATE", "DELETE", "MERGE", "UPSERT",
        "CREATE", "ALTER", "DROP", "TRUNCATE", "RENAME",
        "GRANT", "REVOKE", "DENY",
        "EXEC", "EXECUTE", "CALL", "DO",
        "COPY", "BULK", "BACKUP", "RESTORE",
        "ATTACH", "DETACH", "PRAGMA", "VACUUM", "REINDEX", "CLUSTER",
        "SET", "RESET", "USE", "CHECKPOINT", "SHUTDOWN", "KILL",
        // SELECT ... INTO creates a table on SQL Server/PostgreSQL.
        "INTO",
        // EXPLAIN ANALYZE executes the statement rather than only producing a plan.
        "ANALYZE",
        // SELECT ... FOR UPDATE/SHARE and explicit locks are not read-only.
        "LOCK", "UNLOCK",
    };

    private static readonly HashSet<string> DangerousRoutines = new(StringComparer.OrdinalIgnoreCase)
    {
        "XP_CMDSHELL", "SP_OACREATE", "SP_OAMETHOD", "SP_OADESTROY",
        "OPENROWSET", "OPENQUERY", "OPENDATASOURCE",
        "PG_SLEEP", "PG_READ_FILE", "PG_READ_BINARY_FILE", "PG_LS_DIR", "PG_STAT_FILE",
        "LO_IMPORT", "LO_EXPORT", "DBLINK", "DBLINK_EXEC",
        "LOAD_EXTENSION",
    };

    public static void Validate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new InvalidOperationException("SQL statement is required.");
        if (sql.Length > DbAdminQueryExecutor.MaxSqlLength)
            throw new InvalidOperationException(
                $"SQL exceeds the {DbAdminQueryExecutor.MaxSqlLength}-character limit.");
        if (DbAdminQueryExecutor.ContainsMultipleStatements(sql))
            throw new InvalidOperationException("Read mode accepts exactly one SQL statement.");

        var first = DbAdminQueryExecutor.FirstKeyword(sql);
        if (first is null || !DbAdminQueryExecutor.IsReadOnlyKeyword(first))
            throw new InvalidOperationException(
                first is null
                    ? "Could not detect a SQL keyword in the input."
                    : $"Statement starts with '{first.ToUpperInvariant()}' which is not allowed in read mode.");

        if (SqlStatementInspector.ContainsUnicodeEscapedIdentifier(sql))
            throw new InvalidOperationException(
                "Unicode-escaped identifiers are not allowed in read mode.");

        var dangerousKeyword = SqlStatementInspector.FindFirstIdentifier(
            sql, DangerousKeywords, includeQuoted: false);
        if (dangerousKeyword is not null)
            throw new InvalidOperationException(
                $"Keyword '{dangerousKeyword.ToUpperInvariant()}' is not allowed in read mode.");

        var dangerousRoutine = SqlStatementInspector.FindFirstIdentifier(sql, DangerousRoutines)
                               ?? SqlStatementInspector.FindDynamicDataExporter(sql);
        if (dangerousRoutine is not null)
            throw new InvalidOperationException(
                $"Routine '{dangerousRoutine}' is not allowed in read mode.");
    }

    public static bool ReferencesAnyIdentifier(string sql, IReadOnlySet<string> identifiers)
        => SqlStatementInspector.ReferencesAnyIdentifier(sql, identifiers);

    /// <summary>
    /// True when a query combines a protected table with a provider-specific complete-row
    /// serializer. Deliberately conservative because result columns no longer retain lineage.
    /// </summary>
    public static bool ReferencesWholeRowProjection(
        string sql,
        IReadOnlySet<string> protectedTableIdentifiers)
        => ReferencesAnyIdentifier(sql, protectedTableIdentifiers)
           && (ReferencesAnyIdentifier(sql, WholeRowProjectionIdentifiers)
               || ReferencesIdentifierPair(sql, "FOR", "JSON")
               || ReferencesIdentifierPair(sql, "FOR", "XML"));

    public static bool ReferencesIdentifierPair(string sql, string first, string second)
        => SqlStatementInspector.ReferencesIdentifierPair(sql, first, second);
}
