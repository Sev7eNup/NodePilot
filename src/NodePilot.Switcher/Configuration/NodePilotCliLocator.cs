using System.IO;
using Microsoft.Win32;

namespace NodePilot.Switcher.Configuration;

/// <summary>
/// Finds np.exe on the machine when the configuration does not name it.
///
/// <para>The installation marker is the same one <c>ServiceDiscovery</c> already reads for the
/// service name; it also carries the install path, and both installers place the CLI under
/// <c>tools\np</c> and put that folder on the machine PATH.</para>
/// </summary>
internal static class NodePilotCliLocator
{
    private const string ExecutableName = "np.exe";

    public static IEnumerable<string> Candidates()
    {
        var installPath = ReadInstallPath();
        if (!string.IsNullOrWhiteSpace(installPath))
            yield return Path.Combine(installPath, "tools", "np", ExecutableName);

        // Only the machine PATH: the elevated process also inherits the approving user's PATH, where
        // that user can place any np.exe.
        foreach (var candidate in PathCandidates(
                     Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)))
            yield return candidate;
    }

    internal static IEnumerable<string> PathCandidates(string? path)
    {
        foreach (var directory in PathDirectories(path))
            yield return Path.Combine(directory, ExecutableName);
    }

    private static string? ReadInstallPath()
    {
        try
        {
            using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var marker = localMachine.OpenSubKey(@"SOFTWARE\NodePilot\Server", writable: false);
            return marker?.GetValue("InstallPath") as string;
        }
        catch
        {
            // No marker readable: the PATH lookup below still applies.
            return null;
        }
    }

    private static IEnumerable<string> PathDirectories(string? value)
    {
        foreach (var entry in (value ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // A relative entry would resolve against the working directory.
            if (!Path.IsPathFullyQualified(entry)) continue;
            string full;
            try
            {
                full = Path.GetFullPath(entry);
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry must not abort the search.
                continue;
            }
            yield return full;
        }
    }
}
