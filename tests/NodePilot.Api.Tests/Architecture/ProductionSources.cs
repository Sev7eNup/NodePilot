namespace NodePilot.Api.Tests.Architecture;

/// <summary>
/// Shared source enumeration for the architecture guards that scan production code as text.
///
/// <para>A plain recursive scan of <c>src/</c> would also walk into <c>node_modules</c>,
/// <c>dist</c> and <c>coverage</c> under the npm projects — huge, sometimes unreadable, and
/// irrelevant. C# only lives under <c>src/NodePilot.*</c>, so the walk is scoped there.</para>
/// </summary>
internal static class ProductionSources
{
    /// <summary>Every production <c>.cs</c> file, excluding build output.</summary>
    public static IEnumerable<string> CSharpFiles()
    {
        foreach (var project in ProjectDirectories())
        {
            foreach (var file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            {
                if (HasPathSegment(file, "obj") || HasPathSegment(file, "bin")) continue;
                yield return file;
            }
        }
    }

    /// <summary>The .NET project folders under <c>src/</c> (<c>src/NodePilot.*</c>).</summary>
    public static IReadOnlyList<string> ProjectDirectories()
    {
        var srcDir = Path.Combine(RepoRoot(), "src");
        if (!Directory.Exists(srcDir))
            throw new InvalidOperationException($"production source directory must exist at {srcDir}");

        var projects = Directory.EnumerateDirectories(srcDir, "NodePilot.*", SearchOption.TopDirectoryOnly).ToList();
        if (projects.Count == 0)
            throw new InvalidOperationException($"no src/NodePilot.* project directories found under {srcDir}");
        return projects;
    }

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && dir is not null; depth++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NodePilot.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException($"Could not locate NodePilot.slnx walking up from {AppContext.BaseDirectory}");
    }

    private static bool HasPathSegment(string path, string segment) =>
        path.Contains($"{Path.DirectorySeparatorChar}{segment}{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);
}
