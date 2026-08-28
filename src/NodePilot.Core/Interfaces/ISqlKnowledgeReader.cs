namespace NodePilot.Core.Interfaces;

/// <summary>
/// Read-only, secret-redacted view of the app database schema and query results for the global
/// AI Chat knowledge assistant (the <c>list_db_tables</c>, <c>get_db_table</c> and
/// <c>execute_readonly_sql</c> tools). The LLM translates natural language to SQL; this reader
/// only discovers the schema and runs a single read-only statement. The implementation lives in
/// the API project, reusing <c>DbAdminMetadataService</c> and <c>DbAdminQueryExecutor</c>, and is
/// registered scoped like <see cref="ISettingsKnowledgeReader"/>.
///
/// <para>Redaction is part of the contract: the schema tools omit hidden secret columns
/// (<c>PasswordHash</c>, <c>EncryptedPassword</c>, byte[] blobs) and the four tables holding
/// workflow definitions or custom-activity implementations. SQL referencing those tables is
/// refused; callers use the dedicated RBAC-aware tools instead. Result columns are masked by name
/// and every other cell passes through the audit details redactor, and only <c>string?</c> leaves
/// the reader. Restricted to global Admins at the tool layer.</para>
/// </summary>
public interface ISqlKnowledgeReader
{
    /// <summary>Active SQL dialect token (<c>postgres</c>, <c>sqlserver</c>, ...).</summary>
    string Provider { get; }

    /// <summary>All tracked tables with the columns safe for generic AI discovery.</summary>
    Task<IReadOnlyList<DbTableKnowledgeSummary>> ListTablesAsync(CancellationToken ct);

    /// <summary>One table's AI-safe columns with type/nullable/PK, or null if unknown.</summary>
    Task<DbTableKnowledgeDetail?> GetTableAsync(string name, CancellationToken ct);

    /// <summary>Runs one read-only SQL statement and returns redacted columns and rows. SQL errors
    /// do not throw; they surface as <see cref="SqlQueryKnowledgeResult.Error"/> so the LLM can
    /// correct the query.</summary>
    Task<SqlQueryKnowledgeResult> ExecuteReadAsync(string sql, CancellationToken ct);
}

/// <summary>Compact schema entry for one table: entity name, the real DB table name to use in
/// SQL, primary keys, and the non-hidden column names.</summary>
public sealed record DbTableKnowledgeSummary(
    string Name,
    string DbTableName,
    IReadOnlyList<string> PkColumns,
    IReadOnlyList<string> ColumnNames);

/// <summary>One non-hidden column of a table.</summary>
public sealed record DbColumnKnowledge(
    string Name,
    string ClrType,
    bool IsNullable,
    bool IsPrimaryKey);

/// <summary>Full schema for one table: entity name, DB table name, non-hidden columns, and
/// foreign keys.</summary>
public sealed record DbTableKnowledgeDetail(
    string Name,
    string DbTableName,
    IReadOnlyList<DbColumnKnowledge> Columns,
    IReadOnlyList<DbForeignKeyKnowledge> ForeignKeys);

/// <summary>A foreign-key relationship originating at the described table.</summary>
public sealed record DbForeignKeyKnowledge(
    IReadOnlyList<string> Columns,
    string PrincipalTable,
    IReadOnlyList<string> PrincipalColumns);

/// <summary>Redacted result of a read-only SQL statement. <see cref="Error"/> is non-null when the
/// statement failed (bad SQL, timeout) so the model can retry with a corrected query. On success
/// <see cref="Columns"/> names the result columns and <see cref="Rows"/> holds the redacted cells;
/// hidden or masked columns become <c>"***"</c>.</summary>
public sealed record SqlQueryKnowledgeResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    bool Truncated,
    long DurationMs,
    string? Error);
