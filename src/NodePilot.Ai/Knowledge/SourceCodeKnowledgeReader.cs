using Microsoft.Extensions.Options;

namespace NodePilot.Ai.Knowledge;

/// <summary>Keyword search and file read over the repository source tree. Admin and Operator
/// only, gated at the tool layer.</summary>
public interface ISourceCodeKnowledgeReader
{
    /// <summary>True when the configured source root exists on disk.</summary>
    bool IsAvailable();
    IReadOnlyList<KnowledgeSearchHit> Search(string query);
    KnowledgeFileResult Read(string relPath);
}

/// <summary>
/// Reads the git-tracked source snapshot from <see cref="AiKnowledgeOptions.SourceCodeRootPath"/>
/// (default <c>{AppBaseDir}/knowledge/source</c>, shipped by Build-Artifact.ps1). Four safety layers
/// apply to both search and read, independent of the root: a traversal guard, a secret-file deny
/// list (evaluated first), an extension allowlist (<c>.json</c> is excluded to keep appsettings
/// out), and size and result caps. Every call re-reads the tree, so code changes are picked up.
/// </summary>
public sealed class SourceCodeKnowledgeReader(IOptionsMonitor<AiKnowledgeOptions> options) : ISourceCodeKnowledgeReader
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".md", ".ps1", ".psm1",
        ".csproj", ".props", ".targets", ".sln", ".slnx", ".css", ".scss", ".html",
        ".sql", ".yml", ".yaml", ".razor", ".cshtml", ".sh",
        // .json is excluded because it would expose appsettings*.json.
    };

    private static readonly string[] DeniedNameFragments =
    {
        "appsettings", "jwt-secret", "admin-setup.token", "secret.key",
    };

    private static readonly HashSet<string> DeniedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".key", ".pfx", ".pem", ".p12", ".token",
    };

    private readonly KnowledgeCorpusReader _corpus = new(
        () => options.CurrentValue.SourceCodeRootPath,
        "source",
        IsEligible,
        RejectionFor,
        notFoundError: "Datei nicht gefunden.",
        unreadableError: "Datei konnte nicht gelesen werden.");

    public bool IsAvailable() => _corpus.IsAvailable();

    /// <summary>Applies the deny list first, then the extension allowlist, as a second layer on
    /// top of the git-tracked-only snapshot.</summary>
    internal static bool IsEligible(string path) => !IsDenied(path) && AllowedExtensions.Contains(Path.GetExtension(path));

    internal static bool IsDenied(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name == ".env" || name.StartsWith(".env.", StringComparison.Ordinal)) return true;
        if (DeniedExtensions.Contains(Path.GetExtension(name))) return true;
        foreach (var frag in DeniedNameFragments)
            if (name.Contains(frag, StringComparison.Ordinal)) return true;

        var normalized = "/" + path.Replace('\\', '/').ToLowerInvariant() + "/";
        return normalized.Contains("/data-protection-keys/", StringComparison.Ordinal)
            || normalized.Contains("/.git/", StringComparison.Ordinal);
    }

    /// <summary>Read gates in the same deny-before-allowlist order as <see cref="IsEligible"/>.
    /// Returns null when the file may be read.</summary>
    private static string? RejectionFor(string path)
    {
        if (IsDenied(path)) return "Diese Datei ist gesperrt (Secret-/Konfigurationsdatei).";
        if (!AllowedExtensions.Contains(Path.GetExtension(path))) return "Dieser Dateityp ist nicht lesbar.";
        return null;
    }

    public IReadOnlyList<KnowledgeSearchHit> Search(string query)
    {
        var o = options.CurrentValue;
        return _corpus.Search(query, o.SourceCodeMaxResults, o.SourceCodeMaxFileBytes);
    }

    public KnowledgeFileResult Read(string relPath) =>
        _corpus.Read(relPath, options.CurrentValue.SourceCodeMaxFileBytes);
}
