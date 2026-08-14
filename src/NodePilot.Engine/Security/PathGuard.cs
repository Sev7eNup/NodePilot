using Microsoft.Extensions.Configuration;
using System.Globalization;
using System.Text.Json;

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
///   <item><c>FileSystemOperation:AllowedRoots</c> (optional string array): when non-empty, every
///   path must be lexically inside one of the listed roots and no existing component may be a
///   reparse point. An explicit empty array means no containment restriction.</item>
///   <item>Wildcard characters are rejected by default. Activities that intentionally support
///   globbing must opt in at their specific source parameter.</item>
/// </list>
/// Config keys retain the historical <c>FileSystemOperation:</c> prefix so existing operator
/// docs / appsettings deployments stay valid; the namespace is shared across all path-bearing
/// activities.
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

        string fullPath;
        string fullNormalized;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"File System Operation: path '{path}' is not a valid absolute path: {ex.Message}");
        }

        // Reject existing reparse points even when no containment allow-list is configured.
        // A syntactically-local path can otherwise be a junction to an attacker-controlled UNC
        // share and make the process authenticate over SMB before any allow-root decision runs.
        // ResolveLocalFinalPath is intentionally link-local: it inspects attributes on each path
        // component and never resolves a link target.
        try { fullNormalized = ResolveLocalFinalPath(fullPath); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"File System Operation: path '{path}' traverses an unsafe filesystem component: {ex.Message}");
        }

        var roots = ReadConfiguredRoots(
            config,
            "FileSystemOperation:AllowedRoots",
            out _);
        if (roots.Length == 0) return;

        var allowed = roots.Any(root =>
        {
            if (IsUncPath(root))
                throw new InvalidOperationException(
                    $"File System Operation: configured AllowedRoot '{root}' must not be a UNC or device path");

            return IsWithinRoot(fullNormalized, ResolveLocalFinalPath(Path.GetFullPath(root)));
        });

        if (!allowed)
            throw new InvalidOperationException($"File System Operation: path '{path}' is not within any configured FileSystemOperation:AllowedRoots");
    }

    /// <summary>
    /// Reads an allow-list atomically from the highest-priority provider. Blank, sparse, mixed,
    /// or otherwise malformed arrays are rejected fail-closed. Shared with
    /// <see cref="FileWatcherPathGuard"/> so both guards read their roots the same way.
    /// </summary>
    internal static string[] ReadConfiguredRoots(
        IConfiguration config,
        string sectionPath,
        out bool configured)
    {
        if (config is not IConfigurationRoot root)
            throw new InvalidOperationException(
                $"Security allow-list '{sectionPath}' requires IConfigurationRoot provider metadata");

        // IConfiguration's merged child view does not replace arrays atomically: a one-item
        // runtime override otherwise inherits index 1..N from a lower-priority provider. Read
        // the complete array from the highest-priority provider that declares the section.
        foreach (var provider in root.Providers.Reverse())
        {
            var hasExactValue = provider.TryGet(sectionPath, out var exactValue);
            var childKeys = provider.GetChildKeys([], sectionPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (!hasExactValue && childKeys.Length == 0) continue;

            configured = true;
            if (hasExactValue && childKeys.Length > 0)
                throw MalformedRoots(sectionPath, "contains both a scalar value and indexed children");

            if (hasExactValue)
            {
                // JsonConfigurationProvider represents [] as an exact null entry. It is an
                // atomic provider tombstone: lower-provider indices disappear, while the
                // established AllowedRoots contract still treats the resulting [] as no
                // containment restriction (reparse rejection remains unconditional).
                if (string.IsNullOrWhiteSpace(exactValue)) return [];

                try
                {
                    using var document = JsonDocument.Parse(exactValue);
                    if (document.RootElement.ValueKind != JsonValueKind.Array)
                        throw MalformedRoots(sectionPath, "scalar value is not a JSON string array");

                    var values = new List<string>();
                    foreach (var element in document.RootElement.EnumerateArray())
                    {
                        if (element.ValueKind != JsonValueKind.String
                            || string.IsNullOrWhiteSpace(element.GetString()))
                            throw MalformedRoots(sectionPath, "contains an empty or non-string root");
                        values.Add(element.GetString()!);
                    }
                    return values.ToArray();
                }
                catch (JsonException ex)
                {
                    throw MalformedRoots(sectionPath, $"scalar value is not valid JSON: {ex.Message}");
                }
            }

            var indexed = new SortedDictionary<int, string>();
            foreach (var childKey in childKeys)
            {
                if (!int.TryParse(childKey, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    || index < 0
                    || !provider.TryGet($"{sectionPath}:{childKey}", out var value)
                    || string.IsNullOrWhiteSpace(value)
                    || !indexed.TryAdd(index, value))
                    throw MalformedRoots(sectionPath, "contains malformed, duplicate, or blank indices");
            }

            if (indexed.Keys.Where((index, ordinal) => index != ordinal).Any())
                throw MalformedRoots(sectionPath, "contains sparse array indices");

            return indexed.Values.ToArray();
        }

        configured = false;
        return [];
    }

    private static InvalidOperationException MalformedRoots(string sectionPath, string reason) =>
        new($"Security allow-list '{sectionPath}' is malformed and was rejected: {reason}");

    /// <summary>
    /// Root-containment test shared by both path guards: the path is the root itself, or sits
    /// below it. Both arguments must already be normalized (full path, no trailing separator) —
    /// the separator suffix is what stops <c>C:\Data2</c> from matching the root <c>C:\Data</c>.
    /// </summary>
    internal static bool IsWithinRoot(string normalizedPath, string normalizedRoot)
        => normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
           || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

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

    internal static string ResolveLocalFinalPath(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
            return NormalizeForRootComparison(full);

        var relative = Path.GetRelativePath(root, full);
        var current = root;

        // File.GetAttributes maps to link-local metadata on Windows: unlike Exists followed by
        // ResolveLinkTarget it does not dereference a junction/symlink to discover its target.
        // That property is security-critical for links targeting UNC shares (SMB/NTLM coercion)
        // and also lets us reject dangling links, which Exists reports as false.
        AssertNotReparsePoint(current);
        if (relative == ".") return NormalizeForRootComparison(full);

        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            // Only Zip compression opts into wildcards. It separately restricts them to the
            // leaf component and expands them on the target; no literal filesystem object can
            // exist at or below this segment.
            if (segment.IndexOfAny(WildcardChars) >= 0) break;

            current = Path.Combine(current, segment);
            if (!AssertNotReparsePoint(current)) break;
        }

        return NormalizeForRootComparison(full);
    }

    /// <returns><see langword="true"/> when the path exists; otherwise false.</returns>
    private static bool AssertNotReparsePoint(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"path traverses reparse point '{path}'");

        return true;
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
    internal static bool IsUncPath(string path)
    {
        if (path.Length < 2) return false;
        var c0 = path[0];
        var c1 = path[1];
        return (c0 == '\\' && c1 == '\\')
            || (c0 == '/' && c1 == '/');
    }
}
