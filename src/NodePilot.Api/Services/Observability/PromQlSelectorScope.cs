namespace NodePilot.Api.Services.Observability;

/// <summary>
/// Requires an allowed metric on every label selector, not merely somewhere in the query.
/// Removes strings, comments and label bodies before the caller checks remaining metric tokens.
/// Prometheus remains responsible for the complete PromQL grammar.
/// </summary>
internal static class PromQlSelectorScope
{
    public static bool TryInspect(string query, IReadOnlyList<string> prefixes, out string metricExpression)
    {
        var expression = query.ToCharArray();
        var selector = false;
        string? precedingIdentifier = null;
        for (var i = 0; i < query.Length;)
        {
            var ch = query[i];
            if (char.IsWhiteSpace(ch)) { i++; continue; }
            if (ch == '#')
            {
                while (i < query.Length && query[i] != '\n') expression[i++] = ' ';
                continue;
            }
            if (ch is '\'' or '"' or '`')
            {
                var quote = ch;
                expression[i++] = ' ';
                var closed = false;
                while (i < query.Length)
                {
                    var current = query[i];
                    expression[i++] = ' ';
                    if (current == quote) { closed = true; break; }
                    if (current == '\\' && quote != '`' && i < query.Length)
                        expression[i++] = ' ';
                }
                if (!closed) return Fail(out metricExpression);
                precedingIdentifier = null;
                continue;
            }
            if (ch == '{')
            {
                if (selector || precedingIdentifier is null
                    || precedingIdentifier is "and" or "or" or "unless"
                    || !prefixes.Any(prefix => precedingIdentifier.StartsWith(prefix, StringComparison.Ordinal)))
                    return Fail(out metricExpression);
                selector = true;
                expression[i++] = ' ';
                precedingIdentifier = null;
                continue;
            }
            if (ch == '}')
            {
                if (!selector) return Fail(out metricExpression);
                selector = false;
                expression[i++] = ' ';
                precedingIdentifier = null;
                continue;
            }
            if (selector)
            {
                expression[i++] = ' ';
                continue;
            }
            if (char.IsAsciiLetter(ch) || ch is '_' or ':')
            {
                var start = i++;
                while (i < query.Length && (char.IsAsciiLetterOrDigit(query[i]) || query[i] is '_' or ':')) i++;
                precedingIdentifier = query[start..i];
                continue;
            }
            precedingIdentifier = null;
            i++;
        }
        if (selector) return Fail(out metricExpression);
        metricExpression = new string(expression);
        return true;
    }

    private static bool Fail(out string expression) { expression = string.Empty; return false; }
}
