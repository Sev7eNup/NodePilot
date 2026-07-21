using Microsoft.Extensions.Configuration;

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
///   <item><c>Trigger:FileWatcher:AllowedRoots</c> — string[]. When set, the directory
///   must resolve inside one of these roots.</item>
///   <item><c>Trigger:FileWatcher:AllowSystemPaths</c> — bool, default false. Hard-block
///   on Windows system roots unless this is explicitly enabled.</item>
/// </list>
/// </summary>
public static class FileWatcherPathGuard
{
    private static readonly string[] HardBlockedWindowsRoots =
    {
        @"C:\Windows",
        @"C:\Program Files",
        @"C:\Program Files (x86)",
        @"C:\ProgramData\Microsoft\Crypto",
    };

    public static void Validate(IConfiguration config, string dir)
    {
        string full;
        try { full = Path.GetFullPath(dir); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"FileWatcherTrigger: directory '{dir}' is not a valid path: {ex.Message}");
        }
        var normalized = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (OperatingSystem.IsWindows()
            && !string.Equals(config["Trigger:FileWatcher:AllowSystemPaths"], "true", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var blocked in HardBlockedWindowsRoots)
            {
                if (normalized.Equals(blocked, StringComparison.OrdinalIgnoreCase)
                    || normalized.StartsWith(blocked + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"FileWatcherTrigger: directory '{dir}' is under a system path ('{blocked}'). " +
                        "Set Trigger:FileWatcher:AllowSystemPaths=true and add it to AllowedRoots to override.");
            }
        }

        var roots = config.GetSection("Trigger:FileWatcher:AllowedRoots").GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Cast<string>()
            .ToArray();
        if (roots.Length == 0) return;

        var allowed = roots.Any(root =>
        {
            string rFull;
            try { rFull = Path.GetFullPath(root); } catch { return false; }
            var r = rFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return normalized.Equals(r, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        });
        if (!allowed)
            throw new InvalidOperationException(
                $"FileWatcherTrigger: directory '{dir}' is not within any configured Trigger:FileWatcher:AllowedRoots.");
    }
}
