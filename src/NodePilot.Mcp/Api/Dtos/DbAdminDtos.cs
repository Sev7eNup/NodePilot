namespace NodePilot.Mcp.Api.Dtos;

// DTOs duplicated from src/NodePilot.Api/Services/DbAdmin/DbAdminDtos.cs (no ProjectReference to Api —
// same convention as the CLI). JSON is Web-default (camelCase, case-insensitive).

// ---- DbAdmin (text2sql: schema discovery + read-only SQL execution) ----

public sealed record DbAdminColumnInfo(
    string Name,
    string ClrType,
    bool IsNullable,
    int? MaxLength,
    bool IsPrimaryKey,
    bool IsMasked,
    bool IsReadOnly);

public sealed record DbAdminCapabilities(bool CanUpdate, bool CanDelete);

public sealed record DbAdminTableInfo(
    string Name,
    string DisplayName,
    string DbTableName,
    List<string> PkColumns,
    DbAdminCapabilities Capabilities,
    List<DbAdminColumnInfo> Columns,
    long RowCount,
    List<string> CascadeDeletesTo);

public sealed record DbAdminInfoResponse(
    string Provider,
    bool AllowWriteQueries,
    int QueryTimeoutSeconds,
    int QueryMaxRows);

public sealed record DbAdminQueryRequest(string Sql, string? Mode);

public sealed record DbAdminQueryColumn(string Name, string Type);

public sealed record DbAdminQueryResponse(
    List<DbAdminQueryColumn> Columns,
    List<List<object?>> Rows,
    int? RowsAffected,
    long DurationMs,
    bool Truncated,
    string Mode);