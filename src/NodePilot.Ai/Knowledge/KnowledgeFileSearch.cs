namespace NodePilot.Ai.Knowledge;

/// <summary>One search hit: repo-relative path plus a short context snippet around the first match.</summary>
public sealed record KnowledgeSearchHit(string Path, string Snippet);

/// <summary>Result of reading a single knowledge file: content on success, or an error message.</summary>
public sealed record KnowledgeFileResult(bool Ok, string? Path, string? Content, string? Error)
{
    public static KnowledgeFileResult Fail(string error) => new(false, null, null, error);
    public static KnowledgeFileResult Success(string path, string content) => new(true, path, content, null);
}

/// <summary>
/// Path/traversal guard and keyword file search shared by the docs and source-code knowledge
/// readers. Deliberately NOT the config-bound <c>FileSystemOperation</c> PathGuard — this is a
/// small, self-contained guard scoped to a single knowledge root. Every resolve confines the path
/// under the root (rejects absolute paths, <c>..</c>, and any symlink/normalisation escape).
/// </summary>
internal static class KnowledgeFileSearch
{
    /// <summary>Upper bound on files scanned per search — a cold search over a large source tree is
    /// O(files); this caps the worst case (the caller's per-source result cap bounds the output).</summary>
    private const int ScanCap = 8_000;

    private const int SnippetWindow = 320;

    /// <summary>
    /// Resolves <paramref name="relPath"/> under <paramref name="root"/>, confined to the root.
    /// Returns false (and empty out) for absolute/rooted paths, <c>..</c> escapes, or anything that
    /// normalises outside the root.
    /// </summary>
    public static bool TryResolveWithin(string root, string relPath, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relPath)) return false;
        if (Path.IsPathRooted(relPath)) return false;

        string rootFull, combined;
        try
        {
            rootFull = Path.GetFullPath(root);
            combined = Path.GetFullPath(Path.Combine(rootFull, relPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)) return false;

        fullPath = combined;
        return true;
    }

    /// <summary>Repo-relative, forward-slash path for display (root-relative).</summary>
    public static string RelativeOf(string root, string fullPath)
    {
        try { return Path.GetRelativePath(Path.GetFullPath(root), fullPath).Replace('\\', '/'); }
        catch { return Path.GetFileName(fullPath); }
    }

    /// <summary>
    /// Live keyword search under <paramref name="root"/>: enumerates eligible files
    /// (<paramref name="isEligible"/> on the full path), scores each against the query terms
    /// (filename hit weighted heaviest, then body-occurrence count), and returns the top
    /// <paramref name="maxResults"/> as relative-path + snippet. Reads every candidate fresh, so
    /// changes flow in automatically — no index. Unreadable directories are skipped.
    /// </summary>
    public static IReadOnlyList<KnowledgeSearchHit> Search(
        string root, string query, int maxResults, int maxFileBytes, Func<string, bool> isEligible)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return [];
        var terms = Tokenize(query);
        if (terms.Length == 0) return [];

        var scored = new List<(int Score, KnowledgeSearchHit Hit)>();
        var scanned = 0;

        foreach (var file in WalkFiles(root))
        {
            if (scanned >= ScanCap) break;
            if (!isEligible(file)) continue;
            scanned++;

            string content;
            try
            {
                var info = new FileInfo(file);
                if (info.Length > maxFileBytes) continue;
                content = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            var score = Score(content, Path.GetFileName(file), terms);
            if (score <= 0) continue;

            scored.Add((score, new KnowledgeSearchHit(RelativeOf(root, file), MakeSnippet(content, terms))));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .Take(maxResults)
            .Select(s => s.Hit)
            .ToList();
    }

    private static string[] Tokenize(string query) =>
        (query ?? "")
        .ToLowerInvariant()
        .Split(new[] { ' ', '\t', '\n', '\r', ',', ';', '"', '\'', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries)
        .Where(t => t.Length >= 2)
        .Distinct()
        .Take(12)
        .ToArray();

    private static int Score(string content, string fileName, string[] terms)
    {
        var lowerContent = content.ToLowerInvariant();
        var lowerName = fileName.ToLowerInvariant();
        var score = 0;
        foreach (var t in terms)
        {
            if (lowerName.Contains(t)) score += 8;
            var count = CountOccurrences(lowerContent, t);
            score += Math.Min(count, 20); // cap a single term's body contribution
        }
        return score;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (i >= 0)
        {
            count++;
            i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal);
        }
        return count;
    }

    private static string MakeSnippet(string content, string[] terms)
    {
        var lower = content.ToLowerInvariant();
        var idx = -1;
        foreach (var t in terms)
        {
            var i = lower.IndexOf(t, StringComparison.Ordinal);
            if (i >= 0 && (idx < 0 || i < idx)) idx = i;
        }
        if (idx < 0) idx = 0;

        var start = Math.Max(0, idx - SnippetWindow / 4);
        var len = Math.Min(SnippetWindow, content.Length - start);
        var snippet = content.Substring(start, len)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return (start > 0 ? "…" : "") + snippet + (start + len < content.Length ? "…" : "");
    }

    private static IEnumerable<string> WalkFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { files = []; }
            foreach (var f in files) yield return f;

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { subdirs = []; }
            foreach (var d in subdirs) stack.Push(d);
        }
    }
}
