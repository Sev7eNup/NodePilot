using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodePilot.Data;

namespace NodePilot.Api.Services.DbAdmin;

/// <summary>
/// Executes ad-hoc SQL against the active EF connection on behalf of the admin query
/// console. Two stances:
///
/// <list type="bullet">
///   <item><b>Read</b> — provider-specific read-only transaction wraps the statement so
///   writes can't persist even if the keyword whitelist is bypassed. Defence-in-depth.</item>
///   <item><b>Write</b> — executes inside a normal transaction. Only used when the caller
///   has opted in via <see cref="DbAdminOptions.AllowWriteQueries"/> AND the per-request
///   confirmation header. Persists on success.</item>
/// </list>
///
/// Row-count and statement-text caps are enforced here — controller stays thin. Provider-
/// error sanitisation is left to the controller (it has the HttpContext for correlation IDs).
/// </summary>
public sealed class DbAdminQueryExecutor
{
    public const int MaxSqlLength = 64 * 1024;

    // First-keyword whitelist for read-mode. Defence-in-depth — the read-only transaction
    // is the real guard, this just rejects obviously-wrong inputs before opening a connection.
    private static readonly HashSet<string> ReadOnlyKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WITH", "EXPLAIN", "SHOW", "VALUES", "TABLE",
    };

    private readonly NodePilotDbContext _db;
    private readonly IOptionsMonitor<DbAdminOptions> _options;

    public DbAdminQueryExecutor(NodePilotDbContext db, IOptionsMonitor<DbAdminOptions> options)
    {
        _db = db;
        _options = options;
    }

    public string Provider => ResolveProvider(_db.Database.ProviderName);
    // Monitor.CurrentValue picks up Settings-UI edits without an API restart — bound via
    // services.Configure<DbAdminOptions>(config.GetSection(...)) which auto-reloads.
    public DbAdminOptions Options => _options.CurrentValue;

    /// <summary>
    /// Normalises the EF ProviderName to the short token the UI uses (<c>postgres</c>,
    /// <c>sqlserver</c>, <c>sqlite</c>). Falls back to the raw name when unknown.
    /// </summary>
    public static string ResolveProvider(string? providerName)
    {
        if (string.IsNullOrEmpty(providerName)) return "unknown";
        if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)) return "postgres";
        if (providerName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase)) return "sqlserver";
        if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)) return "sqlite";
        return providerName;
    }

    /// <summary>
    /// Extracts the first SQL keyword from <paramref name="sql"/>, skipping leading whitespace
    /// and line/block comments. Returns <c>null</c> if no keyword could be identified.
    /// </summary>
    public static string? FirstKeyword(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return null;
        var i = 0;
        while (i < sql.Length)
        {
            // Skip whitespace
            while (i < sql.Length && char.IsWhiteSpace(sql[i])) i++;
            if (i >= sql.Length) return null;

            // Skip line comment -- ... \n
            if (i + 1 < sql.Length && sql[i] == '-' && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }

            // Skip block comment /* ... */
            if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/')) i++;
                i = Math.Min(sql.Length, i + 2);
                continue;
            }

            break;
        }

        if (i >= sql.Length) return null;

        var start = i;
        while (i < sql.Length && (char.IsLetter(sql[i]) || sql[i] == '_')) i++;
        return i > start ? sql[start..i] : null;
    }

    public static bool IsReadOnlyKeyword(string keyword) => ReadOnlyKeywords.Contains(keyword);

    /// <summary>
    /// Returns true when <paramref name="sql"/> contains non-comment SQL text after a real
    /// statement terminator. Semicolons inside strings, quoted identifiers, bracket identifiers,
    /// PostgreSQL dollar-quoted strings, and comments are ignored. Trailing semicolons are allowed.
    /// </summary>
    public static bool ContainsMultipleStatements(string sql)
        => CountStatements(sql) > 1;

    /// <summary>
    /// Counts SQL statements while ignoring terminators inside comments, literals and
    /// quoted identifiers. Used for audit metadata so a long batch cannot hide extra
    /// statements beyond the stored preview.
    /// </summary>
    public static int CountStatements(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return 0;

        var count = 0;
        var hasToken = false;
        for (var i = 0; i < sql.Length; i++)
        {
            var ch = sql[i];

            if (ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                SkipLineComment(sql, ref i);
                continue;
            }

            if (ch == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                SkipBlockComment(sql, ref i);
                continue;
            }

            if (ch == '\'')
            {
                hasToken = true;
                SkipQuoted(sql, ref i, '\'');
                continue;
            }

            if (ch == '"')
            {
                hasToken = true;
                SkipQuoted(sql, ref i, '"');
                continue;
            }

            if (ch == '[')
            {
                hasToken = true;
                SkipBracketIdentifier(sql, ref i);
                continue;
            }

            if (ch == '$' && TryReadDollarQuoteTag(sql, i, out var tag))
            {
                hasToken = true;
                SkipDollarQuoted(sql, ref i, tag);
                continue;
            }

            if (ch == ';')
            {
                if (hasToken)
                {
                    count++;
                    hasToken = false;
                }
                continue;
            }

            if (!char.IsWhiteSpace(ch))
                hasToken = true;
        }

        return count + (hasToken ? 1 : 0);
    }

    /// <summary>
    /// Write-mode must never be able to alter the table that proves what write-mode did.
    /// The raw substring check intentionally also catches dynamic-SQL string literals.
    /// A pre-execution forwarded audit event remains the second line of defence.
    /// </summary>
    public static bool ReferencesProtectedAuditStorage(string sql)
        => sql.Contains("AuditLog", StringComparison.OrdinalIgnoreCase);

    public Task<DbAdminQueryResult> ExecuteReadAsync(string sql, CancellationToken ct)
    {
        DbAdminReadOnlySqlGuard.Validate(sql);
        return ExecuteAsync(sql, writeMode: false, ct);
    }

    public Task<DbAdminQueryResult> ExecuteWriteAsync(string sql, CancellationToken ct)
        => ExecuteAsync(sql, writeMode: true, ct);

    private async Task<DbAdminQueryResult> ExecuteAsync(string sql, bool writeMode, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var current = _options.CurrentValue;
        var maxRows = Math.Max(1, current.QueryMaxRows);
        var timeout = Math.Clamp(current.QueryTimeoutSeconds, 1, 600);
        var provider = Provider;

        // Use EF's connection manager so the pool tracks our open/close calls correctly.
        await _db.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = _db.Database.GetDbConnection();
            await using var tx = await conn.BeginTransactionAsync(ct);

            // Postgres has engine-level READ ONLY mode; SQL Server / SQLite don't, so we lean on
            // the transaction + rollback contract plus the keyword whitelist enforced upstream.
            if (!writeMode && provider == "postgres")
            {
                await using var setCmd = conn.CreateCommand();
                setCmd.Transaction = tx;
                setCmd.CommandText = "SET TRANSACTION READ ONLY";
                setCmd.CommandTimeout = timeout;
                await setCmd.ExecuteNonQueryAsync(ct);
            }

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeout;

            await using var reader = await cmd.ExecuteReaderAsync(ct);

            var columns = new List<DbAdminQueryColumn>(reader.FieldCount);
            for (var c = 0; c < reader.FieldCount; c++)
            {
                columns.Add(new DbAdminQueryColumn(
                    Name: reader.GetName(c),
                    Type: FriendlyTypeName(reader.GetFieldType(c))));
            }

            var rows = new List<List<object?>>();
            var truncated = false;
            if (reader.FieldCount > 0)
            {
                while (await reader.ReadAsync(ct))
                {
                    if (rows.Count >= maxRows)
                    {
                        truncated = true;
                        break;
                    }

                    var row = new List<object?>(reader.FieldCount);
                    for (var c = 0; c < reader.FieldCount; c++)
                    {
                        var value = reader.IsDBNull(c) ? null : reader.GetValue(c);
                        row.Add(NormaliseValue(value));
                    }
                    rows.Add(row);
                }
            }

            // DataReader.RecordsAffected is only populated after the reader is fully drained,
            // and is only meaningful for non-SELECT statements. Surface it only for write-mode.
            var rowsAffected = (writeMode && reader.FieldCount == 0) ? reader.RecordsAffected : (int?)null;

            await reader.CloseAsync();

            if (writeMode)
                await tx.CommitAsync(ct);
            else
                await tx.RollbackAsync(ct);

            sw.Stop();
            return new DbAdminQueryResult(
                Columns: columns,
                Rows: rows,
                RowsAffected: rowsAffected,
                DurationMs: sw.ElapsedMilliseconds,
                Truncated: truncated,
                Mode: writeMode ? "write" : "read");
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Coerces provider-native types to JSON-friendly values. byte[] becomes a base64 string,
    /// DateTimeOffset normalises to ISO-8601, everything else passes through.
    /// </summary>
    private static object? NormaliseValue(object? raw) => raw switch
    {
        null => null,
        byte[] bytes => Convert.ToBase64String(bytes),
        DateTimeOffset dto => dto.ToString("O"),
        DateTime dt => dt.ToString("O"),
        TimeSpan ts => ts.ToString("c"),
        _ => raw,
    };

    private static string FriendlyTypeName(Type t)
    {
        if (t == typeof(string)) return "string";
        if (t == typeof(bool)) return "boolean";
        if (t == typeof(int) || t == typeof(short)) return "int";
        if (t == typeof(long)) return "long";
        if (t == typeof(double) || t == typeof(float)) return "double";
        if (t == typeof(decimal)) return "decimal";
        if (t == typeof(Guid)) return "guid";
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return "datetime";
        if (t == typeof(byte[])) return "bytes";
        return t.Name.ToLowerInvariant();
    }

    private static void SkipLineComment(string sql, ref int index)
    {
        index += 2;
        while (index < sql.Length && sql[index] != '\n')
            index++;
    }

    private static void SkipBlockComment(string sql, ref int index)
    {
        index += 2;
        while (index + 1 < sql.Length && !(sql[index] == '*' && sql[index + 1] == '/'))
            index++;

        index = Math.Min(sql.Length - 1, index + 1);
    }

    private static void SkipQuoted(string sql, ref int index, char quote)
    {
        index++;
        while (index < sql.Length)
        {
            if (sql[index] == quote)
            {
                if (index + 1 < sql.Length && sql[index + 1] == quote)
                {
                    index += 2;
                    continue;
                }

                return;
            }

            index++;
        }
    }

    private static void SkipBracketIdentifier(string sql, ref int index)
    {
        index++;
        while (index < sql.Length)
        {
            if (sql[index] == ']')
            {
                if (index + 1 < sql.Length && sql[index + 1] == ']')
                {
                    index += 2;
                    continue;
                }

                return;
            }

            index++;
        }
    }

    private static bool TryReadDollarQuoteTag(string sql, int index, out string tag)
    {
        tag = string.Empty;
        if (sql[index] != '$')
            return false;

        var i = index + 1;
        while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_'))
            i++;

        if (i >= sql.Length || sql[i] != '$')
            return false;

        tag = sql[index..(i + 1)];
        return true;
    }

    private static void SkipDollarQuoted(string sql, ref int index, string tag)
    {
        var end = sql.IndexOf(tag, index + tag.Length, StringComparison.Ordinal);
        index = end < 0 ? sql.Length - 1 : end + tag.Length - 1;
    }
}

public sealed record DbAdminQueryResult(
    List<DbAdminQueryColumn> Columns,
    List<List<object?>> Rows,
    int? RowsAffected,
    long DurationMs,
    bool Truncated,
    string Mode);
