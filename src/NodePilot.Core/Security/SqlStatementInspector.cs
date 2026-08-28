using System.Text;

namespace NodePilot.Core.Security;

/// <summary>
/// Provider-neutral SQL identifier lexer shared by the API and external-agent adapters. It is not a
/// validating SQL parser; it exposes only conservative security predicates, ignores comments and
/// string literals, and keeps quoted identifiers.
/// </summary>
public static class SqlStatementInspector
{
    private const string UnicodeEscapedIdentifierMarker = "<unicode-escaped-identifier>";

    private static readonly HashSet<string> UnicodeEscapedIdentifierMarkers =
        new([UnicodeEscapedIdentifierMarker], StringComparer.Ordinal);

    private static readonly HashSet<string> DynamicDataExporters = new(StringComparer.OrdinalIgnoreCase)
    {
        // PostgreSQL functions that take a table/query/schema/database name (often as a string)
        // and return its data or schema. The source cannot be determined outside that string,
        // so these are never safe on generic read-SQL surfaces.
        "query_to_xml", "table_to_xml", "cursor_to_xml", "schema_to_xml", "database_to_xml",
        "query_to_xmlschema", "table_to_xmlschema", "schema_to_xmlschema", "database_to_xmlschema",
        "query_to_xml_and_xmlschema", "table_to_xml_and_xmlschema",
        "schema_to_xml_and_xmlschema", "database_to_xml_and_xmlschema",
    };

    public static string? FindFirstIdentifier(
        string sql,
        IReadOnlySet<string> identifiers,
        bool includeQuoted = true)
    {
        foreach (var token in Tokenize(sql))
        {
            if ((includeQuoted || !token.Quoted) && identifiers.Contains(token.Value))
                return token.Value;
        }

        return null;
    }

    public static bool ReferencesAnyIdentifier(string sql, IReadOnlySet<string> identifiers)
        => FindFirstIdentifier(sql, identifiers) is not null;

    public static string? FindDynamicDataExporter(string sql)
        => FindFirstIdentifier(sql, DynamicDataExporters);

    /// <summary>
    /// PostgreSQL's U&amp;"..." identifier form can encode any protected name. Agent-facing
    /// lexical policies reject the form instead of duplicating provider unescaping rules.
    /// </summary>
    public static bool ContainsUnicodeEscapedIdentifier(string sql)
        => ReferencesAnyIdentifier(sql, UnicodeEscapedIdentifierMarkers);

    /// <summary>
    /// True when <paramref name="second"/> follows <paramref name="first"/> as consecutive
    /// unquoted identifier tokens. Punctuation and comments are ignored, matching normal SQL
    /// keyword-pair parsing. A quoted identifier breaks the chain.
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

    private static IEnumerable<SqlIdentifier> Tokenize(string sql)
    {
        for (var i = 0; i < sql.Length;)
        {
            if (char.IsWhiteSpace(sql[i]) || sql[i] is ',' or '(' or ')' or '.' or ';' or '*')
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

            if (sql[i] == ':' && i + 1 < sql.Length && sql[i + 1] == ':')
            {
                i += 2;
                yield return new SqlIdentifier("::", Quoted: false);
                continue;
            }

            if (sql[i] == '$' && TryReadDollarQuoteTag(sql, i, out var tag))
            {
                i += tag.Length;
                var end = sql.IndexOf(tag, i, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + tag.Length;
                continue;
            }

            if ((sql[i] == 'U' || sql[i] == 'u')
                && i + 2 < sql.Length
                && sql[i + 1] == '&'
                && sql[i + 2] == '"')
            {
                i += 3;
                _ = ReadEscapedIdentifier(sql, ref i, '"');
                yield return new SqlIdentifier(UnicodeEscapedIdentifierMarker, Quoted: false);
                continue;
            }

            if (sql[i] is '"' or '`')
            {
                var quote = sql[i++];
                var value = ReadEscapedIdentifier(sql, ref i, quote);
                if (value.Length > 0) yield return new SqlIdentifier(value, Quoted: true);
                continue;
            }

            if (sql[i] == '[')
            {
                i++;
                var value = ReadBracketIdentifier(sql, ref i);
                if (value.Length > 0) yield return new SqlIdentifier(value, Quoted: true);
                continue;
            }

            if (char.IsLetter(sql[i]) || sql[i] is '_' or '#')
            {
                var start = i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$' or '#')) i++;
                yield return new SqlIdentifier(sql[start..i], Quoted: false);
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
        tag = string.Empty;
        var end = start + 1;
        while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_')) end++;
        if (end >= sql.Length || sql[end] != '$') return false;
        tag = sql[start..(end + 1)];
        return true;
    }

    private readonly record struct SqlIdentifier(string Value, bool Quoted);
}
