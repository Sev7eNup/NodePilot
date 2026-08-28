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
/// call re-reads the tree, so doc edits take effect without an index or build-time snapshot.
/// Registered as a singleton: pure file IO over the live
/// <see cref="IOptionsMonitor{AiKnowledgeOptions}"/>.
/// </summary>
public sealed class DocsKnowledgeReader(IOptionsMonitor<AiKnowledgeOptions> options) : IDocsKnowledgeReader
{
    private readonly KnowledgeCorpusReader _corpus = new(
        () => options.CurrentValue.DocsRootPath,
        "docs",
        IsMarkdown,
        full => IsMarkdown(full) ? null : "Nur Markdown-Dokumente (.md) sind lesbar.",
        notFoundError: "Dokument nicht gefunden.",
        unreadableError: "Dokument konnte nicht gelesen werden.");

    private static bool IsMarkdown(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

    public bool IsAvailable() => _corpus.IsAvailable();

    public IReadOnlyList<KnowledgeSearchHit> Search(string query)
    {
        var o = options.CurrentValue;
        return _corpus.Search(query, o.DocsMaxResults, o.DocsMaxFileBytes);
    }

    public KnowledgeFileResult Read(string relPath) =>
        _corpus.Read(relPath, options.CurrentValue.DocsMaxFileBytes);
}
