using System.Text.RegularExpressions;
using FluentAssertions;
using NodePilot.Core.Activities;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

/// <summary>
/// The launcher completion exists twice: the SCOrch importer uses the backend map, the designer
/// completes the same names while authoring. A silent drift would mean a name the designer accepts
/// and the engine then rejects at runtime, which is the exact failure the completion removes.
/// </summary>
public class KnownProgramLaunchersFrontendSyncTests
{
    [Fact]
    public void FrontendMirror_MatchesBackendLauncherMap()
    {
        var frontend = ReadFrontendMap();

        frontend.Should().BeEquivalentTo(
            KnownProgramLaunchers.ByName,
            "src/nodepilot-ui/src/lib/knownProgramLaunchers.ts mirrors NodePilot.Core.Activities.KnownProgramLaunchers");
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("CMD")]
    [InlineData("powershell.exe")]
    [InlineData("cscript")]
    [InlineData("wscript.exe")]
    public void TryResolve_CompletesABareLauncherName(string program)
    {
        KnownProgramLaunchers.TryResolve(program, out var resolved).Should().BeTrue();
        Path.IsPathFullyQualified(resolved).Should().BeTrue();
    }

    [Theory]
    [InlineData(@"D:\Tools\cmd.exe", "a launcher that carries its own directory stays untouched")]
    [InlineData(@".\cmd.exe", "a relative path is not a bare name")]
    [InlineData("7z.exe", "only the unambiguous system launchers are completed")]
    [InlineData("", "an empty value has nothing to complete")]
    public void TryResolve_LeavesEverythingElseAlone(string program, string because)
    {
        KnownProgramLaunchers.TryResolve(program, out var resolved).Should().BeFalse(because);
        resolved.Should().BeEmpty();
    }

    private static Dictionary<string, string> ReadFrontendMap()
    {
        var path = Path.Combine(
            FindRepoRoot(), "src", "nodepilot-ui", "src", "lib", "knownProgramLaunchers.ts");
        File.Exists(path).Should().BeTrue($"knownProgramLaunchers.ts must exist at {path}");

        var content = File.ReadAllText(path);
        var block = Regex.Match(
            content,
            @"KNOWN_PROGRAM_LAUNCHERS\s*:\s*Readonly<Record<string,\s*string>>\s*=\s*\{(?<body>[^}]*)\}",
            RegexOptions.Singleline);
        block.Success.Should().BeTrue("knownProgramLaunchers.ts must export KNOWN_PROGRAM_LAUNCHERS as an object literal");

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match entry in Regex.Matches(block.Groups["body"].Value, @"(?<name>\w+)\s*:\s*'(?<path>[^']*)'"))
        {
            // The TS literal escapes every backslash; compare against the C# value, not the source.
            map[entry.Groups["name"].Value] = entry.Groups["path"].Value.Replace(@"\\", @"\");
        }
        return map;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NodePilot.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException(
            $"Could not locate NodePilot.slnx walking up from {AppContext.BaseDirectory}");
    }
}
