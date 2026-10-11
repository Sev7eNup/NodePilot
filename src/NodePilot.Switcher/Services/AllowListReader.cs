using System.IO;
using System.Text;

namespace NodePilot.Switcher.Services;

internal sealed class AllowListReader
{
    public async Task<IReadOnlyList<string>> ReadAsync(string path, CancellationToken cancellationToken)
    {
        string content;
        try
        {
            content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Allowlist could not be read from '{path}': {exception.Message}", exception);
        }

        var entries = Parse(content, path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (entries.Length == 0)
            throw new InvalidOperationException($"Allowlist '{path}' is empty. Empty allowlists are rejected for safety.");

        return entries;
    }

    // Entries are separated by , ; or line breaks. An entry may be wrapped in "..." or '...'
    // (a doubled quote is a literal quote); inside quotes separators belong to the name.
    // A quote only opens at the start of an entry, so Bob's Job stays as written.
    private static List<string> Parse(string content, string path)
    {
        var entries = new List<string>();
        var line = 1;
        var i = 0;

        while (i < content.Length)
        {
            var c = content[i];
            if (c is ' ' or '\t')
            {
                i++;
            }
            else if (c == '\n')
            {
                line++;
                i++;
            }
            else if (c is ',' or ';' or '\r')
            {
                i++;
            }
            else if (c is '"' or '\'')
            {
                entries.Add(ReadQuoted(content, ref i, c, line, path));
            }
            else
            {
                var start = i;
                while (i < content.Length && !IsSeparator(content[i])) i++;
                var entry = content[start..i].Trim();
                if (entry.Length > 0) entries.Add(entry);
            }
        }

        return entries;
    }

    private static string ReadQuoted(string content, ref int i, char quote, int line, string path)
    {
        i++;
        var name = new StringBuilder();
        while (true)
        {
            if (i >= content.Length)
                throw Invalid(path, line, "missing closing quote");

            var c = content[i];
            if (c is '\r' or '\n')
                throw Invalid(path, line, "a quoted name must not span lines");

            if (c != quote)
            {
                name.Append(c);
                i++;
            }
            else if (i + 1 < content.Length && content[i + 1] == quote)
            {
                name.Append(quote);
                i += 2;
            }
            else
            {
                i++;
                break;
            }
        }

        while (i < content.Length && content[i] is ' ' or '\t') i++;
        if (i < content.Length && !IsSeparator(content[i]))
            throw Invalid(path, line, "unexpected text after closing quote");

        var result = name.ToString().Trim();
        if (result.Length == 0)
            throw Invalid(path, line, "quoted name is empty");
        return result;
    }

    private static bool IsSeparator(char c) => c is ',' or ';' or '\r' or '\n';

    private static InvalidOperationException Invalid(string path, int line, string reason) =>
        new($"Allowlist '{path}' line {line}: {reason}.");
}
