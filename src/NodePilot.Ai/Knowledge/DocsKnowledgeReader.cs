using Microsoft.Extensions.Options;

namespace NodePilot.Ai.Knowledge;

/// <summary>Live keyword search + read over the documentation corpus (Markdown).</summary>
public interface IDocsKnowledgeReader
{
    /// <summary>True when the configured docs root exists on disk.</summary>
    bool IsAvailable();
    IReadOnlyList<KnowledgeSearchHit> Search(string query);
    KnowledgeFileResult Read(string relPath);
}

/// <summary>
/// Reads the curated documentation corpus live from <see cref="AiKnowledgeOptions.DocsRootPath"/>
/// (default <c>{AppBaseDir}/knowledge/docs</c>, shipped via the API csproj content copy). Every
/// call re-reads the tree, so doc edits flow in automatically — no index, no build-time snapshot.
/// Singleton: pure file IO reading the live <see cref="IOptionsMonitor{AiKnowledgeOptions}"/>.
/// </summary>
public sealed class DocsKnowledgeReader(IOptionsMonitor<AiKnowledgeOptions> options) : IDocsKnowledgeReader
{
    private string Root()
    {
        var configured = options.CurrentValue.DocsRootPath;
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "knowledge", "docs")
            : configured;
    }

    private static bool IsMarkdown(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

    public bool IsAvailable() => Directory.Exists(Root());

    public IReadOnlyList<KnowledgeSearchHit> Search(string query)
    {
        var o = options.CurrentValue;
        return KnowledgeFileSearch.Search(Root(), query, o.DocsMaxResults, o.DocsMaxFileBytes, IsMarkdown);
    }

    public KnowledgeFileResult Read(string relPath)
    {
        var o = options.CurrentValue;
        var root = Root();
        if (!KnowledgeFileSearch.TryResolveWithin(root, relPath, out var full))
            return KnowledgeFileResult.Fail("Ungültiger oder unerlaubter Pfad.");
        if (!IsMarkdown(full))
            return KnowledgeFileResult.Fail("Nur Markdown-Dokumente (.md) sind lesbar.");
        if (!File.Exists(full))
            return KnowledgeFileResult.Fail("Dokument nicht gefunden.");
        try
        {
            if (new FileInfo(full).Length > o.DocsMaxFileBytes)
                return KnowledgeFileResult.Fail($"Datei zu groß (> {o.DocsMaxFileBytes} Bytes).");
            return KnowledgeFileResult.Success(KnowledgeFileSearch.RelativeOf(root, full), File.ReadAllText(full));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return KnowledgeFileResult.Fail("Dokument konnte nicht gelesen werden.");
        }
    }
}
