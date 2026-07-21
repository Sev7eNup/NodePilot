namespace NodePilot.Api.Services.DbAdmin;

public record DbAdminColumnInfo(
    string Name,
    string ClrType,
    bool IsNullable,
    int? MaxLength,
    bool IsPrimaryKey,
    bool IsMasked,
    bool IsReadOnly
);

public record DbAdminTableInfo(
    string Name,
    string DisplayName,
    string DbTableName,
    List<string> PkColumns,
    DbAdminCapabilities Capabilities,
    List<DbAdminColumnInfo> Columns,
    long RowCount,
    List<string> CascadeDeletesTo
);

public record DbAdminCapabilities(bool CanUpdate, bool CanDelete);

public record DbAdminRowsResponse(long Total, List<Dictionary<string, object?>> Rows);

public record DbAdminPatchRequest(string Column, System.Text.Json.JsonElement Value);

public record DbAdminInfoResponse(
    string Provider,
    bool AllowWriteQueries,
    int QueryTimeoutSeconds,
    int QueryMaxRows
);

public record DbAdminQueryRequest(string Sql, string? Mode);

public record DbAdminQueryColumn(string Name, string Type);

public record DbAdminQueryResponse(
    List<DbAdminQueryColumn> Columns,
    List<List<object?>> Rows,
    int? RowsAffected,
    long DurationMs,
    bool Truncated,
    string Mode
);

public record DbAdminQueryError(string Code, string Message, string? CorrelationId);
