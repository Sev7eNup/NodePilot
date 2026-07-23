using System.Diagnostics;
using NodePilot.Api.Services.DbAdmin;
using NodePilot.Core.Audit;
using NodePilot.Core.Interfaces;

namespace NodePilot.Api.Ai;

/// <summary>
/// <see cref="ISqlKnowledgeReader"/> over the existing DbAdmin services. Reuses
/// <see cref="DbAdminMetadataService"/> (singleton — schema is stable) for the catalog and
/// <see cref="DbAdminQueryExecutor"/> (scoped — owns the request DbContext) for read-only execution,
/// then redacts every cell before it leaves the reader. Scoped, matching
/// <see cref="SettingsKnowledgeReader"/>.
///
/// <para><b>Redaction (two layers):</b> first, any result column whose name matches a schema column
/// marked <c>IsHidden</c> (PasswordHash, EncryptedPassword, byte[] blobs) or the masked
/// GlobalVariable.Value has its whole column replaced with <c>"***"</c>; second, every remaining cell
/// is stringified and run through <see cref="IAuditDetailsRedactor"/>. Result rows are capped (token
/// budget) and cells truncated. Only <c>string?</c> ever leaves this reader.</para>
///
/// <para>This closes the secret-leak gap that raw SQL otherwise opens: even if the model selects a
/// secret column, it gets <c>"***"</c>. Caveat: an aliased secret column (<c>SELECT PasswordHash AS p</c>)
/// can't be mapped back to its source column, so the redactor is the fallback for such cases — the
/// tool prompt instructs the model not to select secret columns, and the tool is Admin/Operator-gated.</para>
/// </summary>
public sealed class SqlKnowledgeReader : ISqlKnowledgeReader
{
    private const int MaxRows = 200;
    private const int MaxCellChars = 500;

    private static readonly string[] MaskedColumnNames = ["Value"]; // GlobalVariable.Value is masked (not hidden) in DbAdminPolicy.

    private readonly DbAdminMetadataService _metadata;
    private readonly DbAdminQueryExecutor _executor;
    private readonly IAuditDetailsRedactor _redactor;

    // Result-column names that must never leave the reader unredacted. Built once: every column
    // flagged IsHidden in any table, plus the masked-by-name set above.
    private readonly HashSet<string> _secretColumnNames;

    public SqlKnowledgeReader(DbAdminMetadataService metadata, DbAdminQueryExecutor executor, IAuditDetailsRedactor redactor)
    {
        _metadata = metadata;
        _executor = executor;
        _redactor = redactor;
        _secretColumnNames = BuildSecretColumnNames();
    }

    private HashSet<string> BuildSecretColumnNames()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in _metadata.GetAllTables())
        {
            foreach (var c in t.Columns)
            {
                if (c.IsHidden) set.Add(c.Name);
                else if (t.Name == "GlobalVariable" && c.Name == "Value") set.Add(c.Name);
            }
        }
        foreach (var n in MaskedColumnNames) set.Add(n);
        return set;
    }

    public Task<IReadOnlyList<DbTableKnowledgeSummary>> ListTablesAsync(CancellationToken ct)
    {
        var rows = _metadata.GetAllTables()
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new DbTableKnowledgeSummary(
                t.Name,
                t.DbTableName,
                t.PkColumns,
                t.Columns.Where(c => !c.IsHidden).Select(c => c.Name).ToList()))
            .ToList();
        return Task.FromResult<IReadOnlyList<DbTableKnowledgeSummary>>(rows);
    }

    public Task<DbTableKnowledgeDetail?> GetTableAsync(string name, CancellationToken ct)
    {
        var t = _metadata.GetTable(name);
        if (t is null) return Task.FromResult<DbTableKnowledgeDetail?>(null);
        var cols = t.Columns
            .Where(c => !c.IsHidden)
            .Select(c => new DbColumnKnowledge(c.Name, FriendlyType(c), c.IsNullable, c.IsPrimaryKey))
            .ToList();
        return Task.FromResult<DbTableKnowledgeDetail?>(new DbTableKnowledgeDetail(t.Name, t.DbTableName, cols));
    }

    public async Task<SqlQueryKnowledgeResult> ExecuteReadAsync(string sql, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
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
        // Per-column mask: indices of result columns whose name is a known secret column.
        var masked = new bool[columns.Count];
        for (var i = 0; i < columns.Count; i++)
            masked[i] = _secretColumnNames.Contains(columns[i]);

        var rows = new List<IReadOnlyList<string?>>(result.Rows.Count);
        var truncated = result.Truncated;
        foreach (var row in result.Rows)
        {
            if (rows.Count >= MaxRows) { truncated = true; break; }
            var cells = new string?[row.Count];
            for (var c = 0; c < row.Count && c < columns.Count; c++)
            {
                if (masked[c]) { cells[c] = "***"; continue; }
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