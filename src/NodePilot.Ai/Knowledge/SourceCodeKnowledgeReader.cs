using Microsoft.Extensions.Options;

namespace NodePilot.Ai.Knowledge;

/// <summary>Live keyword search + read over the repository source tree (Admin/Operator only, gated at the tool layer).</summary>
public interface ISourceCodeKnowledgeReader
{
    /// <summary>True when the configured source root exists on disk.</summary>
    bool IsAvailable();
    IReadOnlyList<KnowledgeSearchHit> Search(string query);
    KnowledgeFileResult Read(string relPath);
}

/// <summary>
/// Reads the git-tracked source snapshot live from <see cref="AiKnowledgeOptions.SourceCodeRootPath"/>
/// (default <c>{AppBaseDir}/knowledge/source</c>, shipped by Build-Artifact.ps1). Four safety layers
/// apply at BOTH search and read, independent of the root: a traversal guard, a secret-file DENY
/// list (evaluated first), an extension allowlist (<c>.json</c> deliberately excluded to keep
/// appsettings out), and size/result caps. Every call re-reads the tree, so code changes flow in
/// automatically.
/// </summary>
public sealed class SourceCodeKnowledgeReader(IOptionsMonitor<AiKnowledgeOptions> options) : ISourceCodeKnowledgeReader
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".md", ".ps1", ".psm1",
        ".csproj", ".props", ".targets", ".sln", ".slnx", ".css", ".scss", ".html",
        ".sql", ".yml", ".yaml", ".razor", ".cshtml", ".sh",
        // .json is deliberately excluded — it would expose appsettings*.json.
    };

    private static readonly string[] DeniedNameFragments =
    {
        "appsettings", "jwt-secret", "admin-setup.token", "secret.key",
    };

    private static readonly HashSet<string> DeniedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".key", ".pfx", ".pem", ".p12", ".token",
    };

    private string Root()
    {
        var configured = options.CurrentValue.SourceCodeRootPath;
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "knowledge", "source")
            : configured;
    }

    public bool IsAvailable() => Directory.Exists(Root());

    /// <summary>DENY first (belt-and-suspenders on top of the git-tracked-only snapshot), then extension allowlist.</summary>
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

    public IReadOnlyList<KnowledgeSearchHit> Search(string query)
    {
        var o = options.CurrentValue;
        return KnowledgeFileSearch.Search(Root(), query, o.SourceCodeMaxResults, o.SourceCodeMaxFileBytes, IsEligible);
    }

    public KnowledgeFileResult Read(string relPath)
    {
        var o = options.CurrentValue;
        var root = Root();
        if (!KnowledgeFileSearch.TryResolveWithin(root, relPath, out var full))
            return KnowledgeFileResult.Fail("Ungültiger oder unerlaubter Pfad.");
        if (IsDenied(full))
            return KnowledgeFileResult.Fail("Diese Datei ist gesperrt (Secret-/Konfigurationsdatei).");
        if (!AllowedExtensions.Contains(Path.GetExtension(full)))
            return KnowledgeFileResult.Fail("Dieser Dateityp ist nicht lesbar.");
        if (!File.Exists(full))
            return KnowledgeFileResult.Fail("Datei nicht gefunden.");
        try
        {
            if (new FileInfo(full).Length > o.SourceCodeMaxFileBytes)
                return KnowledgeFileResult.Fail($"Datei zu groß (> {o.SourceCodeMaxFileBytes} Bytes).");
            return KnowledgeFileResult.Success(KnowledgeFileSearch.RelativeOf(root, full), File.ReadAllText(full));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return KnowledgeFileResult.Fail("Datei konnte nicht gelesen werden.");
        }
    }
}
