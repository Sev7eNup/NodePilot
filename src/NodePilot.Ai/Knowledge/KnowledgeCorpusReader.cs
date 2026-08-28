namespace NodePilot.Ai.Knowledge;

/// <summary>
/// Corpus mechanics shared by <see cref="DocsKnowledgeReader"/> and
/// <see cref="SourceCodeKnowledgeReader"/>: root resolution (configured value, else
/// <c>{AppBaseDir}/knowledge/&lt;folder&gt;</c>), availability, keyword search, and a guarded read
/// (traversal guard, then corpus eligibility gates, existence, size cap, IO). Each corpus brings
/// its own eligibility rules and rejection wording; only the order of the gates is shared.
/// </summary>
internal sealed class KnowledgeCorpusReader(
    Func<string?> configuredRoot,
    string defaultFolderName,
    Func<string, bool> isEligible,
    Func<string, string?> readRejection,
    string notFoundError,
    string unreadableError)
{
    /// <summary>Traversal rejection message; the guard is root-agnostic, so it is shared.</summary>
    private const string InvalidPathError = "Ungültiger oder unerlaubter Pfad.";

    public string Root
    {
        get
        {
            var configured = configuredRoot();
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(AppContext.BaseDirectory, "knowledge", defaultFolderName)
                : configured;
        }
    }

    public bool IsAvailable() => Directory.Exists(Root);

    public IReadOnlyList<KnowledgeSearchHit> Search(string query, int maxResults, int maxFileBytes) =>
        KnowledgeFileSearch.Search(Root, query, maxResults, maxFileBytes, isEligible);

    public KnowledgeFileResult Read(string relPath, int maxFileBytes)
    {
        var root = Root;
        if (!KnowledgeFileSearch.TryResolveWithin(root, relPath, out var full))
            return KnowledgeFileResult.Fail(InvalidPathError);
        if (readRejection(full) is { } rejection)
            return KnowledgeFileResult.Fail(rejection);
        if (!File.Exists(full))
            return KnowledgeFileResult.Fail(notFoundError);
        try
        {
            if (new FileInfo(full).Length > maxFileBytes)
                return KnowledgeFileResult.Fail($"Datei zu groß (> {maxFileBytes} Bytes).");
            return KnowledgeFileResult.Success(KnowledgeFileSearch.RelativeOf(root, full), File.ReadAllText(full));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return KnowledgeFileResult.Fail(unreadableError);
        }
    }
}
