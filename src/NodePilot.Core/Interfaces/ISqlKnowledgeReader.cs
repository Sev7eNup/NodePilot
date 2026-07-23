namespace NodePilot.Core.Interfaces;

/// <summary>
/// Read-only, secret-redacted view of the NodePilot App-DB schema + query results for the global
/// "AI Chat" knowledge assistant (the <c>list_db_tables</c> / <c>get_db_table</c> /
/// <c>execute_readonly_sql</c> tools). Text2sql: the LLM translates natural language to SQL, this
/// reader only discovers the schema and runs a single read-only statement. Implementation lives in
/// the API project (it reuses <c>DbAdminMetadataService</c> + <c>DbAdminQueryExecutor</c>), registered
/// scoped — exactly the <see cref="ISettingsKnowledgeReader"/> pattern.
///
/// <para><b>Redaction is the contract</b>: schema tools omit hidden secret columns
/// (<c>PasswordHash</c>, <c>EncryptedPassword</c>, byte[] blobs); query results redact any column
/// whose name matches a hidden/masked column in the schema to <c>"***"</c> and run every cell through
/// the audit details redactor. Only <c>string?</c> leaves the reader — never raw <c>object?</c> — so
/// the model never sees an unredacted value. Restricted to Admin/Operator at the tool layer.</para>
/// </summary>
public interface ISqlKnowledgeReader
{
    /// <summary>Active SQL dialect token (<c>postgres</c>, <c>sqlserver</c>, ...).</summary>
    string Provider { get; }

    /// <summary>All tracked tables, with their non-hidden columns named. Secret columns are omitted.</summary>
    Task<IReadOnlyList<DbTableKnowledgeSummary>> ListTablesAsync(CancellationToken ct);

    /// <summary>One table's non-hidden columns with type/nullable/PK, or null if unknown. Secret columns omitted.</summary>
    Task<DbTableKnowledgeDetail?> GetTableAsync(string name, CancellationToken ct);

    /// <summary>Runs a single read-only SQL statement and returns redacted columns + rows. Never throws for
    /// SQL errors — they surface as <see cref="SqlQueryKnowledgeResult.Error"/> so the LLM can correct the query.</summary>
    Task<SqlQueryKnowledgeResult> ExecuteReadAsync(string sql, CancellationToken ct);
}

/// <summary>Compact schema entry for one table: its entity name, the real DB table name (pluralised —
/// use this in SQL), primary keys, and the non-hidden column names.</summary>
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

/// <summary>Full schema for one table: its entity name, the real DB table name, and its non-hidden columns.</summary>
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
/// statement failed to execute (bad SQL, timeout, …) so the model can retry with a corrected query;
/// on success <see cref="Columns"/> names the result columns and <see cref="Rows"/> holds the redacted
/// cells (hidden/masked columns become <c>"***"</c>).</summary>
public sealed record SqlQueryKnowledgeResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    bool Truncated,
    long DurationMs,
    string? Error);
