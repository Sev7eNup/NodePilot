namespace NodePilot.Core.Activities;

/// <summary>
/// The handful of Windows launchers a `startProgram` author may name without a directory.
///
/// <para>`filePath` must be a fully qualified path — the engine launches through CreateProcess and
/// never searches the target's PATH, so a bare name would resolve differently per machine and could
/// not be checked against <c>FileSystemOperation:AllowedRoots</c>. These four are completed to their
/// system locations instead of being rejected, both on SCOrch import and while authoring in the
/// designer, so the stored definition always carries the absolute path.</para>
///
/// <para>Mirrored in <c>src/nodepilot-ui/src/lib/knownProgramLaunchers.ts</c>; the two are pinned
/// together by <c>KnownProgramLaunchersFrontendSyncTests</c>.</para>
/// </summary>
public static class KnownProgramLaunchers
{
    public const string CmdExe = @"C:\Windows\System32\cmd.exe";
    public const string PowerShellExe = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
    public const string CScriptExe = @"C:\Windows\System32\cscript.exe";
    public const string WScriptExe = @"C:\Windows\System32\wscript.exe";

    /// <summary>Launcher stem (no extension) to its absolute path.</summary>
    public static readonly IReadOnlyDictionary<string, string> ByName =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cmd"] = CmdExe,
            ["powershell"] = PowerShellExe,
            ["cscript"] = CScriptExe,
            ["wscript"] = WScriptExe,
        };

    /// <summary>
    /// Completes a bare launcher name to its absolute path. Returns false for anything that
    /// carries a directory of its own, so a deliberate path to another copy stays untouched.
    /// </summary>
    public static bool TryResolve(string? program, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(program)) return false;

        var value = program.Trim();
        if (value.Contains('\\') || value.Contains('/') || value.Contains(':')) return false;

        var stem = Path.GetFileNameWithoutExtension(value);
        if (!ByName.TryGetValue(stem, out var path)) return false;

        resolved = path;
        return true;
    }
}
