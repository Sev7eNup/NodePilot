using Microsoft.Extensions.Configuration;

namespace NodePilot.Engine.Security;

/// <summary>
/// Optional validation for file paths passed to <c>FileOperationActivity</c>,
/// <c>FolderOperationActivity</c>, and the file-mode of <c>XmlQuery</c>/<c>JsonQuery</c>.
/// <list type="bullet">
///   <item>UNC paths (<c>\\server\share</c>, <c>//server/share</c>) are <b>always</b> rejected
///   regardless of any flag — they coerce the WinRM target into outbound SMB to an attacker-
///   controlled host (NTLMv2 relay, capture, exfiltration). No legitimate workflow needs to
///   express a UNC path here; mount the share inside the workflow first if you really must.</item>
///   <item><c>..</c> traversal is rejected when <c>FileSystemOperation:RejectTraversal=true</c>
///   (default since Phase 3 hardening). Setting it to <c>false</c> tolerates relative navigation
///   for legacy admin scripts but is no longer the recommended posture.</item>
///   <item><c>FileSystemOperation:AllowedRoots</c> (optional string array): when set, every path must
///   resolve inside one of the listed roots regardless of traversal.</item>
///   <item>Wildcard characters are rejected by default. Activities that intentionally support
///   globbing must opt in at their specific source parameter.</item>
/// </list>
/// Config keys retain the historical <c>FileSystemOperation:</c> prefix so existing operator
/// docs / appsettings deployments stay valid; the namespace is shared across all path-bearing
/// activities.
///
/// AllowedRoots final-path resolution is local to the NodePilot host. Remote WinRM targets do not
/// expose their reparse-point map to this guard; remote workflows still need target-side ACLs and
/// constrained working directories as the authoritative boundary.
/// </summary>
public static class PathGuard
{
    private static readonly char[] WildcardChars = ['*', '?'];

    private static readonly char[] InvalidLeafNameChars =
    [
        '<', '>', ':', '"', '/', '\\', '|', '?', '*'
    ];

    private static readonly HashSet<string> ReservedWindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static void Validate(IConfiguration config, string path, bool allowWildcards = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("File System Operation: path is empty");

        // UNC reject is unconditional. We check this BEFORE traversal so the error message is
        // more accurate when both apply (e.g. \\server\share\..\foo).
        if (IsUncPath(path))
            throw new InvalidOperationException(
                $"File System Operation: UNC path '{path}' is not allowed. " +
                "UNC paths can be coerced into outbound SMB to attacker-controlled hosts " +
                "(NTLMv2 relay / hash capture). Mount the share inside the workflow first " +
                "if remote access is genuinely required.");

        if (!allowWildcards && path.IndexOfAny(WildcardChars) >= 0)
            throw new InvalidOperationException(
                $"File System Operation: path '{path}' contains wildcard characters. " +
                "File-system activities operate on literal paths only; use an explicit list step " +
                "before mutating multiple files.");

        // Default-on since Phase 3: traversal is rejected unless an operator explicitly opts
        // out. The previous default tolerated `..` so legacy admin scripts kept working; the
        // hardening default now matches the production template.
        var rejectTraversalRaw = config["FileSystemOperation:RejectTraversal"];
        var rejectTraversal = string.IsNullOrWhiteSpace(rejectTraversalRaw)
            || string.Equals(rejectTraversalRaw, "true", StringComparison.OrdinalIgnoreCase);
        if (rejectTraversal && ContainsTraversal(path))
            throw new InvalidOperationException($"File System Operation: path '{path}' contains '..' traversal (blocked by FileSystemOperation:RejectTraversal)");

        var roots = config.GetSection("FileSystemOperation:AllowedRoots")
            .GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Cast<string>()
            .ToArray();
        if (roots.Length > 0)
        {
            string fullPath;
            string fullNormalized;
            try { fullPath = Path.GetFullPath(path); }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"File System Operation: path '{path}' is not a valid absolute path: {ex.Message}");
            }

            try { fullNormalized = ResolveLocalFinalPath(fullPath); }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"File System Operation: path '{path}' final path could not be resolved: {ex.Message}");
            }

            var allowed = roots.Any(root =>
            {
                var r = ResolveLocalFinalPath(Path.GetFullPath(root));
                return fullNormalized.Equals(r, StringComparison.OrdinalIgnoreCase)
                    || fullNormalized.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            });

            if (!allowed)
                throw new InvalidOperationException($"File System Operation: path '{path}' is not within any configured FileSystemOperation:AllowedRoots");
        }
    }

    public static void ValidateSiblingRenameTarget(IConfiguration config, string currentPath, string newName)
    {
        ValidateLeafName(newName);
        Validate(config, BuildSiblingPath(currentPath, newName), allowWildcards: false);
    }

    public static void ValidateLeafName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("File System Operation: newName is empty");

        if (name is "." or "..")
            throw new InvalidOperationException("File System Operation: newName must be a plain file or folder name, not a relative path segment");

        if (name.EndsWith(' ') || name.EndsWith('.'))
            throw new InvalidOperationException("File System Operation: newName must not end with a space or dot");

        if (name.Any(char.IsControl) || name.IndexOfAny(InvalidLeafNameChars) >= 0)
            throw new InvalidOperationException("File System Operation: newName must not contain path separators, drive prefixes, wildcards, or characters invalid on Windows");

        var baseName = name.Split('.')[0];
        if (ReservedWindowsDeviceNames.Contains(baseName))
            throw new InvalidOperationException("File System Operation: newName uses a reserved Windows device name");
    }

    private static string BuildSiblingPath(string currentPath, string leafName)
    {
        if (string.IsNullOrWhiteSpace(currentPath)) return leafName;

        var slash = currentPath.LastIndexOf('/');
        var backslash = currentPath.LastIndexOf('\\');
        var idx = Math.Max(slash, backslash);
        if (idx < 0) return leafName;

        var separator = currentPath[idx];
        if (idx == 0) return separator + leafName;

        var parent = currentPath[..idx];
        if (idx == 2 && currentPath[1] == ':')
            parent = currentPath[..(idx + 1)];

        return parent.EndsWith(separator)
            ? parent + leafName
            : parent + separator + leafName;
    }

    private static bool ContainsTraversal(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.Contains("/../") || normalized.EndsWith("/..") || normalized.StartsWith("../"))
            return true;
        return normalized == "..";
    }

    private static string ResolveLocalFinalPath(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return NormalizeForRootComparison(full);

        var relative = Path.GetRelativePath(root, full);
        var current = ResolveExistingPath(root);
        if (relative == ".")
            return NormalizeForRootComparison(current);

        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);
            if (Directory.Exists(current) || File.Exists(current))
            {
                current = ResolveExistingPath(current);
                continue;
            }

            for (var j = i + 1; j < segments.Length; j++)
                current = Path.Combine(current, segments[j]);
            return NormalizeForRootComparison(Path.GetFullPath(current));
        }

        return NormalizeForRootComparison(Path.GetFullPath(current));
    }

    private static string ResolveExistingPath(string path)
    {
        if (Directory.Exists(path))
        {
            var info = new DirectoryInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
                return info.FullName;
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName;
        }

        var file = new FileInfo(path);
        if ((file.Attributes & FileAttributes.ReparsePoint) == 0)
            return file.FullName;
        return file.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file.FullName;
    }

    private static string NormalizeForRootComparison(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Detects UNC paths in either Windows backslash form (<c>\\server\share\…</c>) or
    /// forward-slash form (<c>//server/share/…</c>, which the .NET path APIs accept on Windows
    /// and which an attacker could use to slip through a naive backslash-only check).
    /// Local extended-length paths (<c>\\?\C:\…</c>, <c>\\.\PIPE\…</c>) and network
    /// extended-length paths (<c>\\?\UNC\server\share\…</c>) are also flagged: the device-
    /// namespace prefix is not a path component our workflows have any reason to express.
    /// </summary>
    private static bool IsUncPath(string path)
    {
        if (path.Length < 2) return false;
        var c0 = path[0];
        var c1 = path[1];
        return (c0 == '\\' && c1 == '\\')
            || (c0 == '/' && c1 == '/');
    }
}
