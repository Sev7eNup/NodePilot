using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using NodePilot.Mcp.Api;
using NodePilot.Mcp.Api.Dtos;
using NodePilot.Mcp.Mapping;

namespace NodePilot.Mcp.Tools;

/// <summary>
/// Read-only text2sql surface: exposes the NodePilot App-DB schema and runs single read-only SQL
/// statements against it. The agent does the natural-language → SQL translation; these tools only
/// provide schema discovery + safe read execution. All endpoints are Admin-only server-side.
///
/// Security stance:
/// - No write tool. The API's /api/dbadmin/query rejects anything outside the read keyword
///   whitelist (SELECT/WITH/EXPLAIN/SHOW/VALUES/TABLE), enforces single-statement, rolls back the
///   (read-only) transaction, and caps rows + timeout.
/// - Hidden secret columns (PasswordHash, EncryptedPassword, byte[]) never appear in list_db_tables;
///   GlobalVariable.Value is masked as "***".
/// - Raw SQL via run_readonly_sql CAN select those secret columns (the read executor returns raw
///   rows) — this matches the existing Admin-only DB-Admin UI. Do NOT select secret columns; rely on
///   list_db_tables for the safe schema. (OutputRedactor wiring for /query is a tracked follow-up.)
/// </summary>
[McpServerToolType]
public sealed class DbAdminMcpTools
{
    private readonly NodePilotApiClient _api;

    public DbAdminMcpTools(NodePilotApiClient api) => _api = api;

    /// <summary>Max rows + bytes surfaced from run_readonly_sql to keep tool output inside MCP caps.</summary>
    private const int MaxResultRows = 200;

    private const int MaxResultChars = 4000;

    [McpServerTool(Name = "list_db_tables", ReadOnly = true)]
    [Description("List the NodePilot App-DB schema (every EF-tracked table with its non-hidden columns, primary keys and row count). Hidden secret columns are excluded; GlobalVariable.Value is masked. Pass `name` to filter to one table (case-insensitive). This is the safe schema source — prefer it over guessing column names for run_readonly_sql. Admin-only.")]
    public async Task<object> ListDbTables(
        [Description("Optional table-name filter (case-insensitive substring). Omit for all tables.")] string? name = null,
        CancellationToken cancellationToken = default)
    {
        var tables = await ApiErrorMapper.Guard(() => _api.ListDbTablesAsync(cancellationToken));

        IEnumerable<DbAdminTableInfo> filtered = tables;
        if (!string.IsNullOrWhiteSpace(name))
        {
            var needle = name.Trim();
            filtered = tables.Where(t => t.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        var rows = filtered.OrderBy(t => t.Name).Select(t => new
        {
            name = t.Name,
            displayName = t.DisplayName,
            dbTableName = t.DbTableName,
            pkColumns = t.PkColumns,
            rowCount = t.RowCount,
            columns = t.Columns.Select(c => new
            {
                name = c.Name,
                type = c.ClrType,
                isNullable = c.IsNullable,
                isPrimaryKey = c.IsPrimaryKey,
                isMasked = c.IsMasked,
            }),
        });

        return new { tables = rows };
    }

    [McpServerTool(Name = "get_db_info", ReadOnly = true)]
    [Description("Return the App-DB provider (postgres/sqlserver) and the read-query limits (maxRows, timeoutSeconds) so you can write queries that stay within them. Admin-only.")]
    public async Task<object> GetDbInfo(CancellationToken cancellationToken = default)
    {
        var info = await ApiErrorMapper.Guard(() => _api.GetDbInfoAsync(cancellationToken));
        return new
        {
            provider = info.Provider,
            allowWriteQueries = info.AllowWriteQueries,
            queryMaxRows = info.QueryMaxRows,
            queryTimeoutSeconds = info.QueryTimeoutSeconds,
            hint = "run_readonly_sql only accepts read statements (SELECT/WITH/EXPLAIN/SHOW/VALUES/TABLE). Writes are not exposed.",
        };
    }

    [McpServerTool(Name = "run_readonly_sql", ReadOnly = true)]
    [Description("Run a single read-only SQL statement against the NodePilot App-DB and return columns + rows. Only SELECT/WITH/EXPLAIN/SHOW/VALUES/TABLE first-keyword statements are accepted (server-enforced); the transaction is read-only and rolled back. Use list_db_tables first for the schema. Do NOT select secret columns (PasswordHash, EncryptedPassword) — they are hidden in list_db_tables but reachable via raw SQL. Results are capped (max 200 rows / 4 KB). Admin-only.")]
    public async Task<object> RunReadonlySql(
        [Description("A single read-only SQL statement (SELECT/WITH/EXPLAIN/SHOW/VALUES/TABLE).")] string sql,
        CancellationToken cancellationToken = default)
    {
        var result = await ApiErrorMapper.Guard(() => _api.ExecuteDbReadQueryAsync(sql, cancellationToken));

        var rows = result.Rows;
        var truncated = result.Truncated;
        if (rows.Count > MaxResultRows)
        {
            rows = rows.Take(MaxResultRows).ToList();
            truncated = true;
        }

        // Keep the whole tool response inside a sane byte budget — if the serialized form still
        // overruns after the row cap, drop the rows and return a hint to narrow the query.
        var candidate = new
        {
            columns = result.Columns,
            rows,
            rowsAffected = result.RowsAffected,
            durationMs = result.DurationMs,
            truncated,
            rowCount = rows.Count,
        };
        var json = JsonSerializer.Serialize(candidate, NodePilotApiClient.JsonOptions);
        if (json.Length <= MaxResultChars)
            return candidate;

        return new
        {
            columns = result.Columns,
            rows = Array.Empty<List<object?>>(),
            rowsAffected = result.RowsAffected,
            durationMs = result.DurationMs,
            truncated = true,
            rowCount = result.Rows.Count,
            note = "Result too large — rows dropped to stay inside the MCP tool-output cap. Narrow your query (fewer columns / WHERE / LIMIT).",
        };
    }
}