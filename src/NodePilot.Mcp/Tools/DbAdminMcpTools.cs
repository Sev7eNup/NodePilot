using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using NodePilot.Core.Security;
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
///   GlobalVariable.Value is masked as "***". The shared external-agent policy additionally removes
///   Workflow Definitions, custom-activity scripts and executable parameter defaults.
/// - Raw SQL cannot reach them either: /api/dbadmin/query rejects a read statement that names a
///   protected column, masks protected result columns of a wildcard select as "***", and rejects a
///   whole-row serializer over a table that holds a secret column (to_json/row_to_json/::text/
///   FOR JSON — these carry the row past the two name-based layers). Use list_db_tables for the
///   safe schema.
/// - MCP additionally rejects every SQL reference to the four opaque automation tables before the
///   HTTP request and masks matching result-column names in depth. Browser DbAdmin remains forensic.
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
    [Description("List the NodePilot App-DB schema (every EF-tracked table with its agent-safe columns, primary keys and row count). Hidden secrets and opaque workflow/custom-activity implementation payloads are excluded; GlobalVariable.Value is masked. Pass `name` to filter one table. Prefer this schema over guessing column names. Admin-only.")]
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

        var rows = filtered
            .Where(t => ExternalAgentSqlPolicy.IsSchemaTableVisible(t.Name))
            .OrderBy(t => t.Name)
            .Select(t => new
        {
            name = t.Name,
            displayName = t.DisplayName,
            dbTableName = t.DbTableName,
            pkColumns = t.PkColumns,
            rowCount = t.RowCount,
            columns = t.Columns
                .Where(c => ExternalAgentSqlPolicy.IsSchemaColumnVisible(t.Name, c.Name))
                .Select(c => new
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
    [Description("Run one read-only SQL statement against the NodePilot App-DB. The server enforces read-only SQL. MCP additionally rejects opaque Workflow Definition and custom-activity implementation payloads; use their dedicated tools instead. Sensitive result-column names are masked in depth. Results are capped (max 200 rows / 4 KB). Admin-only.")]
    public async Task<object> RunReadonlySql(
        [Description("A single read-only SQL statement (SELECT/WITH/EXPLAIN/SHOW/VALUES/TABLE).")] string sql,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new McpException("SQL statement is required.");
        if (ExternalAgentSqlPolicy.ReferencesProtectedProjection(sql))
            throw new McpException(ExternalAgentSqlPolicy.RejectionMessage);

        var result = await ApiErrorMapper.Guard(() => _api.ExecuteDbReadQueryAsync(sql, cancellationToken));

        // The API endpoint remains a raw forensic DbAdmin surface. MCP is an external-agent
        // boundary, so it reapplies the shared result-name mask before tool output is serialized.
        for (var c = 0; c < result.Columns.Count; c++)
        {
            if (!ExternalAgentSqlPolicy.IsProtectedResultColumn(result.Columns[c].Name))
                continue;

            foreach (var row in result.Rows)
            {
                if (c < row.Count)
                    row[c] = ExternalAgentSqlPolicy.Mask;
            }
        }

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
