using System.Diagnostics;
using NodePilot.Api.Services.DbAdmin;
using NodePilot.Core.Audit;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Security;

namespace NodePilot.Api.Ai;

/// <summary>
/// <see cref="ISqlKnowledgeReader"/> over the existing DbAdmin services. Reuses
/// <see cref="DbAdminMetadataService"/> (singleton — schema is stable) for the catalog and
/// <see cref="DbAdminQueryExecutor"/> (scoped — owns the request DbContext) for read-only execution,
/// then redacts every cell before it leaves the reader. Tables holding Workflow Definitions or
/// custom-activity implementations are excluded from this generic source and remain available only
/// through dedicated, RBAC-aware tools.
/// Scoped, matching <see cref="SettingsKnowledgeReader"/>.
///
/// <para><b>Redaction layers:</b> the external-agent policy first removes and rejects opaque
/// automation tables. Then <see cref="DbAdminSecretColumns"/> refuses statements that name a protected
/// column, masks protected result columns, and rejects whole-row serializers over protected tables.
/// Finally, every remaining cell is stringified and run through <see cref="IAuditDetailsRedactor"/>.
/// Result rows are capped (token budget) and cells truncated. Only <c>string?</c> ever leaves this
/// reader.</para>
///
/// <para>The shared secret-column guard also runs on <c>/api/dbadmin/query</c>. The external-agent
/// table policy intentionally does not: DbAdmin keeps those rows visible to administrators
/// for forensic inspection and never forwards its response to an LLM.</para>
/// </summary>
public sealed class SqlKnowledgeReader : ISqlKnowledgeReader
{
    private const int MaxRows = 200;
    private const int MaxCellChars = 500;

    private readonly DbAdminMetadataService _metadata;
    private readonly DbAdminQueryExecutor _executor;
    private readonly IAuditDetailsRedactor _redactor;
    private readonly DbAdminSecretColumns _secretColumns;

    public SqlKnowledgeReader(
        DbAdminMetadataService metadata,
        DbAdminQueryExecutor executor,
        IAuditDetailsRedactor redactor,
        DbAdminSecretColumns secretColumns)
    {
        _metadata = metadata;
        _executor = executor;
        _redactor = redactor;
        _secretColumns = secretColumns;
    }

    public string Provider => _executor.Provider;

    public Task<IReadOnlyList<DbTableKnowledgeSummary>> ListTablesAsync(CancellationToken ct)
    {
        var rows = _metadata.GetAllTables()
            .Where(t => ExternalAgentSqlPolicy.IsSchemaTableVisible(t.Name))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new DbTableKnowledgeSummary(
                t.Name,
                t.DbTableName,
                t.PkColumns,
                t.Columns
                    .Where(c => !c.IsHidden
                                && ExternalAgentSqlPolicy.IsSchemaColumnVisible(t.Name, c.Name))
                    .Select(c => c.Name)
                    .ToList()))
            .ToList();
        return Task.FromResult<IReadOnlyList<DbTableKnowledgeSummary>>(rows);
    }

    public Task<DbTableKnowledgeDetail?> GetTableAsync(string name, CancellationToken ct)
    {
        var t = _metadata.GetTable(name);
        if (t is null || !ExternalAgentSqlPolicy.IsSchemaTableVisible(t.Name))
            return Task.FromResult<DbTableKnowledgeDetail?>(null);
        var cols = t.Columns
            .Where(c => !c.IsHidden
                        && ExternalAgentSqlPolicy.IsSchemaColumnVisible(t.Name, c.Name))
            .Select(c => new DbColumnKnowledge(c.Name, FriendlyType(c), c.IsNullable, c.IsPrimaryKey))
            .ToList();
        var visibleNames = cols.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var foreignKeys = t.ForeignKeys
            .Where(fk => fk.Columns.All(visibleNames.Contains))
            .Select(fk => new DbForeignKeyKnowledge(
                fk.Columns, fk.PrincipalDbTableName, fk.PrincipalColumns))
            .ToList();
        return Task.FromResult<DbTableKnowledgeDetail?>(
            new DbTableKnowledgeDetail(t.Name, t.DbTableName, cols, foreignKeys));
    }

    public async Task<SqlQueryKnowledgeResult> ExecuteReadAsync(string sql, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // DbAdmin keeps opaque automation rows visible for forensic administrators. This reader
        // sends results to an external LLM, so it rejects every mention of those tables before a
        // database connection opens. Dedicated tools own RBAC and payload redaction.
        if (ExternalAgentSqlPolicy.ReferencesProtectedProjection(sql))
        {
            return new SqlQueryKnowledgeResult(
                Array.Empty<string>(), Array.Empty<IReadOnlyList<string?>>(), false,
                sw.ElapsedMilliseconds, ExternalAgentSqlPolicy.RejectionMessage);
        }

        // Result-column masking cannot recover source lineage after aliases/expressions, so a
        // statement that mentions a protected identifier is refused before it reaches the database.
        if (_secretColumns.ReferencesProtectedColumn(sql))
        {
            return new SqlQueryKnowledgeResult(
                Array.Empty<string>(), Array.Empty<IReadOnlyList<string?>>(), false,
                sw.ElapsedMilliseconds, "Query references a protected column.");
        }

        // Same refusal for whole-row serializers, which carry the secret past the column mask
        // without ever naming it. The error text doubles as the correction hint the model acts on.
        if (_secretColumns.ReferencesProtectedRowProjection(sql))
        {
            return new SqlQueryKnowledgeResult(
                Array.Empty<string>(), Array.Empty<IReadOnlyList<string?>>(), false,
                sw.ElapsedMilliseconds,
                "Query serializes a whole row of a table that holds secret columns "
                + "(to_json/row_to_json/::text/FOR JSON). List the columns you need explicitly.");
        }

        DbAdminQueryResult result;
        try
        {
            result = await _executor.ExecuteReadAsync(sql, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Bad SQL, timeout, multi-statement, … — surface as Error so the model can correct the query.
            return new SqlQueryKnowledgeResult(Array.Empty<string>(), Array.Empty<IReadOnlyList<string?>>(), false, sw.ElapsedMilliseconds, ex.Message);
        }

        var columns = result.Columns.Select(c => c.Name).ToList();
        var masked = _secretColumns.BuildColumnMask(columns);
        for (var c = 0; c < columns.Count; c++)
        {
            if (ExternalAgentSqlPolicy.IsProtectedResultColumn(columns[c]))
                masked[c] = true;
        }

        var rows = new List<IReadOnlyList<string?>>(result.Rows.Count);
        var truncated = result.Truncated;
        foreach (var row in result.Rows)
        {
            if (rows.Count >= MaxRows) { truncated = true; break; }
            var cells = new string?[row.Count];
            for (var c = 0; c < row.Count && c < columns.Count; c++)
            {
                if (masked[c]) { cells[c] = DbAdminSecretColumns.Mask; continue; }
                cells[c] = RedactCell(row[c]);
            }
            rows.Add(cells);
        }

        return new SqlQueryKnowledgeResult(columns, rows, truncated, result.DurationMs, null);
    }

    private string? RedactCell(object? value)
    {
        if (value is null) return null;
        var s = value switch
        {
            bool b => b ? "true" : "false",
            _ => value.ToString() ?? string.Empty,
        };
        if (s.Length > MaxCellChars) s = s[..MaxCellChars] + "…";
        return _redactor.Redact(s);
    }

    private static string FriendlyType(ColumnMeta c)
    {
        var t = c.ClrType;
        var name = t.Name;
        if (c.IsNullable && Nullable.GetUnderlyingType(t) is null && !t.IsClass) name += "?";
        return name;
    }
}
