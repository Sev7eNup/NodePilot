using System.Text;

namespace NodePilot.Api.Services.DbAdmin;

/// <summary>
/// Provider-neutral, fail-closed lexical guard applied at the executor boundary. This is deliberately
/// below every controller/tool adapter so callers cannot bypass the read policy by reusing the executor
/// directly. The database read-only transaction remains the second line of defence.
/// </summary>
internal static class DbAdminReadOnlySqlGuard
{
    /// <summary>Pseudo-token emitted by the tokenizer for PostgreSQL's <c>::</c> cast operator.</summary>
    public const string CastOperator = "::";

    private static readonly HashSet<string> DangerousKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        // REPLACE is deliberately absent: it is a standard string function on every supported
        // backend (`SELECT REPLACE(Name,'a','b')`). The MySQL `REPLACE INTO` write form it would
        // guard against is not a supported backend, is already blocked by the INTO token below,
        // and could never pass FirstKeyword/IsReadOnlyKeyword as a leading keyword either.
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

        foreach (var token in Tokenize(sql))
        {
            if (!token.Quoted && DangerousKeywords.Contains(token.Value))
                throw new InvalidOperationException(
                    $"Keyword '{token.Value.ToUpperInvariant()}' is not allowed in read mode.");
            if (DangerousRoutines.Contains(token.Value))
                throw new InvalidOperationException(
                    $"Routine '{token.Value}' is not allowed in read mode.");
        }
    }

    public static bool ReferencesAnyIdentifier(string sql, IReadOnlySet<string> identifiers)
        => Tokenize(sql).Any(token => identifiers.Contains(token.Value));

    /// <summary>
    /// True when <paramref name="second"/> follows <paramref name="first"/> as consecutive unquoted
    /// tokens — the shape needed to spot multi-word constructs such as SQL Server's
    /// <c>FOR JSON</c>. A quoted identifier breaks the chain so <c>"FOR" JSON</c> (two identifiers
    /// that merely look like the keyword pair) does not match.
    /// </summary>
    public static bool ReferencesIdentifierPair(string sql, string first, string second)
    {
        string? previous = null;
        foreach (var token in Tokenize(sql))
        {
            if (previous is not null
                && !token.Quoted
                && previous.Equals(first, StringComparison.OrdinalIgnoreCase)
                && token.Value.Equals(second, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            previous = token.Quoted ? null : token.Value;
        }
        return false;
    }

    private static IEnumerable<SqlToken> Tokenize(string sql)
    {
        for (var i = 0; i < sql.Length;)
        {
            if (char.IsWhiteSpace(sql[i]) || sql[i] is ',' or '(' or ')' or '.' or ';')
            {
                i++;
                continue;
            }

            if (i + 1 < sql.Length && sql[i] == '-' && sql[i + 1] == '-')
            {
                i += 2;
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }

            if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*')
            {
                i += 2;
                var depth = 1;
                while (i < sql.Length && depth > 0)
                {
                    if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*')
                    {
                        depth++;
                        i += 2;
                    }
                    else if (i + 1 < sql.Length && sql[i] == '*' && sql[i + 1] == '/')
                    {
                        depth--;
                        i += 2;
                    }
                    else i++;
                }
                continue;
            }

            if (sql[i] == '\'')
            {
                SkipQuotedLiteral(sql, ref i, '\'');
                continue;
            }

            // PostgreSQL's cast operator, emitted as its own token. `SELECT u::text FROM "Users" u`
            // casts a whole ROW to text and therefore returns every column — including the hidden
            // ones — under a harmless result-column name. The row-projection guard needs to see it;
            // it is not a keyword, so Validate's keyword/routine checks ignore it.
            if (sql[i] == ':' && i + 1 < sql.Length && sql[i + 1] == ':')
            {
                i += 2;
                yield return new SqlToken(CastOperator, Quoted: false);
                continue;
            }

            if (sql[i] == '$' && TryReadDollarQuoteTag(sql, i, out var tag))
            {
                i += tag.Length;
                var end = sql.IndexOf(tag, i, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + tag.Length;
                continue;
            }

            if (sql[i] is '"' or '`')
            {
                var quote = sql[i++];
                var value = ReadEscapedIdentifier(sql, ref i, quote);
                if (value.Length > 0) yield return new SqlToken(value, Quoted: true);
                continue;
            }

            if (sql[i] == '[')
            {
                i++;
                var value = ReadBracketIdentifier(sql, ref i);
                if (value.Length > 0) yield return new SqlToken(value, Quoted: true);
                continue;
            }

            if (char.IsLetter(sql[i]) || sql[i] is '_' or '#')
            {
                var start = i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$' or '#')) i++;
                yield return new SqlToken(sql[start..i], Quoted: false);
                continue;
            }

            i++;
        }
    }

    private static void SkipQuotedLiteral(string sql, ref int i, char quote)
    {
        i++;
        while (i < sql.Length)
        {
            if (sql[i] != quote) { i++; continue; }
            if (i + 1 < sql.Length && sql[i + 1] == quote) { i += 2; continue; }
            i++;
            return;
        }
    }

    private static string ReadEscapedIdentifier(string sql, ref int i, char quote)
    {
        var value = new StringBuilder();
        while (i < sql.Length)
        {
            if (sql[i] != quote) { value.Append(sql[i++]); continue; }
            if (i + 1 < sql.Length && sql[i + 1] == quote)
            {
                value.Append(quote);
                i += 2;
                continue;
            }
            i++;
            break;
        }
        return value.ToString();
    }

    private static string ReadBracketIdentifier(string sql, ref int i)
    {
        var value = new StringBuilder();
        while (i < sql.Length)
        {
            if (sql[i] != ']') { value.Append(sql[i++]); continue; }
            if (i + 1 < sql.Length && sql[i + 1] == ']')
            {
                value.Append(']');
                i += 2;
                continue;
            }
            i++;
            break;
        }
        return value.ToString();
    }

    private static bool TryReadDollarQuoteTag(string sql, int start, out string tag)
    {
        tag = "";
        if (sql[start] != '$') return false;
        var end = start + 1;
        while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_')) end++;
        if (end >= sql.Length || sql[end] != '$') return false;
        tag = sql[start..(end + 1)];
        return true;
    }

    private readonly record struct SqlToken(string Value, bool Quoted);
}
