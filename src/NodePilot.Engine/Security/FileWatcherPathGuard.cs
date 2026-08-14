using Microsoft.Extensions.Configuration;
using System.IO.Enumeration;
using System.Net;
using System.Net.NetworkInformation;

namespace NodePilot.Engine.Security;

/// <summary>
/// Shared directory-allow-list check for the FileWatcher trigger family.
/// Both the scheduler-side source (background subscription) and the engine-side
/// manual executor (one-shot directory scan) must apply the same guard — otherwise
/// a workflow author could enumerate <c>C:\Windows\System32</c> via a manual run
/// even though the background trigger would have refused to start there.
///
/// Config keys (historical, kept stable for operator docs):
/// <list type="bullet">
///   <item><c>Trigger:FileWatcher:AllowedRoots</c> — string[]. When non-empty, the directory
///   must be lexically inside one of these roots; reparse points are always rejected.</item>
///   <item><c>Trigger:FileWatcher:AllowSystemPaths</c> — bool, default false. Hard-block
///   on Windows system roots unless this is explicitly enabled.</item>
/// </list>
/// </summary>
public static class FileWatcherPathGuard
{
    private static readonly string[] HardBlockedWindowsRoots = BuildHardBlockedWindowsRoots();

    public static void Validate(IConfiguration config, string dir)
    {
        RejectWindowsDeviceNamespace(dir, "directory");
        var allowSystemPaths = OperatingSystem.IsWindows()
            && string.Equals(
                config["Trigger:FileWatcher:AllowSystemPaths"],
                "true",
                StringComparison.OrdinalIgnoreCase);

        string full;
        try
        {
            full = Path.GetFullPath(CanonicalizeLocalAdministrativeShareForPolicy(
                dir,
                rejectUnmappedLocalShare: !allowSystemPaths));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"FileWatcherTrigger: directory '{dir}' is not a valid path: {ex.Message}");
        }
        string normalized;
        try { normalized = PathGuard.ResolveLocalFinalPath(full); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"FileWatcherTrigger: directory '{dir}' final path could not be resolved: {ex.Message}");
        }

        if (OperatingSystem.IsWindows() && !allowSystemPaths)
        {
            foreach (var blocked in HardBlockedWindowsRoots)
            {
                // A parent such as C:\ is just as capable of exposing C:\Windows when
                // IncludeSubdirectories is enabled. Keep the default policy conservative
                // for non-recursive watches too: the watched root may not intersect a
                // protected system tree in either direction.
                if (PathGuard.IsWithinRoot(normalized, blocked) ||
                    PathGuard.IsWithinRoot(blocked, normalized))
                    throw new InvalidOperationException(
                        $"FileWatcherTrigger: directory '{dir}' intersects a system path ('{blocked}'). " +
                        "Set Trigger:FileWatcher:AllowSystemPaths=true and add it to AllowedRoots to override.");
            }
        }

        var roots = PathGuard.ReadConfiguredRoots(
            config,
            "Trigger:FileWatcher:AllowedRoots",
            out _);
        if (roots.Length == 0) return;

        foreach (var root in roots)
            RejectWindowsDeviceNamespace(root, "configured AllowedRoot");

        var allowed = roots.Any(root =>
        {
            string rFull;
            try
            {
                rFull = Path.GetFullPath(CanonicalizeLocalAdministrativeShareForPolicy(
                    root,
                    rejectUnmappedLocalShare: !allowSystemPaths));
            }
            catch { return false; }
            string r;
            try { r = PathGuard.ResolveLocalFinalPath(rFull); } catch { return false; }
            return PathGuard.IsWithinRoot(normalized, r);
        });
        if (!allowed)
            throw new InvalidOperationException(
                $"FileWatcherTrigger: directory '{dir}' is not within any configured Trigger:FileWatcher:AllowedRoots.");
    }

    /// <summary>
    /// Enumerates a manual FileWatcher scan without asking <see cref="Directory.GetFiles(string, string, SearchOption)"/>
    /// to recurse through the tree. Each directory is inspected link-locally before it is
    /// enumerated, and reparse points are rejected rather than followed.
    /// </summary>
    public static IReadOnlyList<string> EnumerateFilesReparseFree(
        string root,
        string searchPattern,
        bool includeSubdirectories)
    {
        var files = new List<string>();
        WalkReparseFree(
            root,
            includeSubdirectories,
            path =>
            {
                if (MatchesSearchPattern(searchPattern, Path.GetFileName(path)))
                    files.Add(path);
            });
        return files;
    }

    /// <summary>
    /// Preflights the subtree used by <see cref="FileSystemWatcher.IncludeSubdirectories"/>.
    /// This prevents a pre-existing child junction from extending a configured watched root.
    /// A concurrent link creation/rename after the walk remains an OS-level race; emitted event
    /// paths are revalidated by the scheduler before dispatch as a second line of defence.
    /// </summary>
    public static void ValidateReparseFreeSubtree(string root) =>
        WalkReparseFree(root, includeSubdirectories: true, onFile: null);

    private static void WalkReparseFree(
        string root,
        bool includeSubdirectories,
        Action<string>? onFile)
    {
        RejectWindowsDeviceNamespace(root, "directory");
        _ = PathGuard.ResolveLocalFinalPath(Path.GetFullPath(root));
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            _ = PathGuard.ResolveLocalFinalPath(directory);

            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         directory, "*", SearchOption.TopDirectoryOnly))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"FileWatcherTrigger: unable to inspect '{entry}' safely: {ex.Message}", ex);
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException(
                        $"FileWatcherTrigger: watched tree contains reparse point '{entry}'.");

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (includeSubdirectories) pending.Push(entry);
                    continue;
                }

                onFile?.Invoke(entry);
            }
        }
    }

    private static bool MatchesSearchPattern(string searchPattern, string fileName)
    {
        var translated = FileSystemName.TranslateWin32Expression(
            string.IsNullOrWhiteSpace(searchPattern) ? "*.*" : searchPattern);
        return FileSystemName.MatchesWin32Expression(
            translated,
            fileName,
            ignoreCase: OperatingSystem.IsWindows());
    }

    private static void RejectWindowsDeviceNamespace(string path, string label)
    {
        if (!OperatingSystem.IsWindows()) return;

        // FileWatcher intentionally supports ordinary UNC shares (\\server\share), but Win32
        // device/extended namespaces are never a valid workflow input. In particular,
        // \\?\C:\Windows remains textually outside the C:\Windows hard-block comparison while
        // FileSystemWatcher still accepts it, bypassing the system-path policy.
        var windowsPath = path.Replace('/', '\\');
        if (windowsPath.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            windowsPath.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            windowsPath.StartsWith(@"\??\", StringComparison.Ordinal) ||
            windowsPath.StartsWith(@"\\??\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"FileWatcherTrigger: {label} '{path}' uses a Windows device namespace, which is not allowed.");
        }
    }

    /// <summary>
    /// Maps a local-machine administrative UNC share to its local filesystem spelling for
    /// policy comparisons. FileWatcher deliberately permits remote UNC shares, but paths such
    /// as <c>\\localhost\c$\Windows</c> name the local system tree and must hit the same
    /// hard-block/AllowedRoots decisions as <c>C:\Windows</c>.
    /// </summary>
    internal static string CanonicalizeLocalAdministrativeShareForPolicy(
        string path,
        bool rejectUnmappedLocalShare)
    {
        if (!OperatingSystem.IsWindows()) return path;

        var windowsPath = path.Replace('/', '\\');
        if (!windowsPath.StartsWith(@"\\", StringComparison.Ordinal) ||
            windowsPath.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            windowsPath.StartsWith(@"\\.\", StringComparison.Ordinal))
            return path;

        // Apply Win32/SMB share-root semantics before mapping the share to a local
        // directory. In particular, ADMIN$\.. is clamped to ADMIN$ by Windows; mapping
        // first would incorrectly turn it into C:\Windows\.. and miss the hard-block.
        // This also canonicalizes repeated separators and the accepted trailing-dot/
        // trailing-space spellings of administrative share roots.
        windowsPath = Path.GetFullPath(windowsPath);

        var serverEnd = windowsPath.IndexOf('\\', 2);
        if (serverEnd <= 2) return path;
        var shareStart = serverEnd + 1;
        // Win32 accepts and normalizes repeated separators between the server and
        // share (for example \\localhost\\c$). Skip the empty UNC segments here so
        // they cannot make the policy parser see an empty share while the watcher
        // later opens the local administrative share.
        while (shareStart < windowsPath.Length && windowsPath[shareStart] == '\\')
            shareStart++;
        if (shareStart == windowsPath.Length) return path;

        var shareEnd = windowsPath.IndexOf('\\', shareStart);
        var server = windowsPath[2..serverEnd];
        var share = shareEnd < 0
            ? windowsPath[shareStart..]
            : windowsPath[shareStart..shareEnd];
        if (!IsLocalServerAlias(server)) return path;

        string? localRoot = null;
        if (share.Length == 2 && share[1] == '$' && char.IsAsciiLetter(share[0]))
        {
            localRoot = $"{char.ToUpperInvariant(share[0])}:\\";
        }
        else if (share.Equals("ADMIN$", StringComparison.OrdinalIgnoreCase))
        {
            localRoot = Environment.GetEnvironmentVariable("SystemRoot");
            if (string.IsNullOrWhiteSpace(localRoot))
                localRoot = Path.GetDirectoryName(Environment.SystemDirectory);
        }

        if (string.IsNullOrWhiteSpace(localRoot))
        {
            if (rejectUnmappedLocalShare)
            {
                throw new InvalidOperationException(
                    $"local UNC share '{share}' cannot be mapped safely while " +
                    "Trigger:FileWatcher:AllowSystemPaths is disabled");
            }

            return windowsPath;
        }
        if (shareEnd < 0 || shareEnd == windowsPath.Length - 1)
            return Path.GetFullPath(localRoot);

        var relative = windowsPath[(shareEnd + 1)..].TrimStart('\\');
        return Path.GetFullPath(Path.Combine(localRoot, relative));
    }

    private static bool IsLocalServerAlias(string server)
    {
        var normalizedServer = server.Trim().TrimEnd('.');
        if (normalizedServer.Length > 1 && normalizedServer[0] == '[' && normalizedServer[^1] == ']')
            normalizedServer = normalizedServer[1..^1];

        if (normalizedServer.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            normalizedServer.Equals("localhost.localdomain", StringComparison.OrdinalIgnoreCase))
            return true;

        IPAddress? address;
        if (IPAddress.TryParse(normalizedServer, out address) ||
            TryParseWindowsIpv6LiteralHost(normalizedServer, out address))
        {
            if (address is null) return false;
            address = NormalizeMappedIpv4Address(address);
            if (IPAddress.IsLoopback(address)) return true;
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .SelectMany(networkInterface =>
                        networkInterface.GetIPProperties().UnicastAddresses)
                    .Any(unicast =>
                        NormalizeMappedIpv4Address(unicast.Address).Equals(address));
            }
            catch (NetworkInformationException)
            {
                return false;
            }
        }

        return LocalServerAliases.Value.Contains(normalizedServer);
    }

    private static bool TryParseWindowsIpv6LiteralHost(string server, out IPAddress? address)
    {
        const string suffix = ".ipv6-literal.net";
        address = null;
        if (!server.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;

        // Windows represents IPv6 UNC servers without brackets: ':' becomes '-' and a
        // scope-id '%' becomes 's', e.g. ::1 => --1.ipv6-literal.net.
        var encoded = server[..^suffix.Length];
        var decoded = encoded.Replace('-', ':').Replace('s', '%').Replace('S', '%');
        return IPAddress.TryParse(decoded, out address);
    }

    private static IPAddress NormalizeMappedIpv4Address(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static readonly Lazy<HashSet<string>> LocalServerAliases = new(
        static () =>
        {
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Environment.MachineName,
            };
            try
            {
                var dnsHost = Dns.GetHostName();
                if (!string.IsNullOrWhiteSpace(dnsHost)) aliases.Add(dnsHost.TrimEnd('.'));
            }
            catch { /* Environment.MachineName remains authoritative. */ }

            try
            {
                var properties = IPGlobalProperties.GetIPGlobalProperties();
                if (!string.IsNullOrWhiteSpace(properties.HostName))
                {
                    aliases.Add(properties.HostName.TrimEnd('.'));
                    if (!string.IsNullOrWhiteSpace(properties.DomainName))
                        aliases.Add($"{properties.HostName}.{properties.DomainName}".TrimEnd('.'));
                }
            }
            catch (NetworkInformationException) { }
            return aliases;
        },
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static string[] BuildHardBlockedWindowsRoots()
    {
        if (!OperatingSystem.IsWindows()) return [];

        var configuredSystemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        var systemRootFromSystemDirectory = Path.GetDirectoryName(Environment.SystemDirectory);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return new[]
            {
                configuredSystemRoot,
                systemRootFromSystemDirectory,
                programFiles,
                programFilesX86,
                string.IsNullOrWhiteSpace(programData)
                    ? null
                    : Path.Combine(programData, "Microsoft", "Crypto"),
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
