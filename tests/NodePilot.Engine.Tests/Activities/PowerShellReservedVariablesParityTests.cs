using System.Text.RegularExpressions;
using FluentAssertions;
using NodePilot.Core.Activities;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

/// <summary>
/// The reserved-variable contract has three consumers that must agree: the script wrapper (which
/// withholds these names from injection and capture), the static data-bus analysis (which drives
/// the designer's variable picker) and the frontend mirror of that analysis. Nothing generates one
/// from the other, so a name added on one side would silently make the picker advertise a variable
/// the runtime never publishes.
/// </summary>
public class PowerShellReservedVariablesParityTests
{
    [Fact]
    public void FrontendMirror_MatchesTheBackendContract()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "nodepilot-ui", "src", "lib", "upstreamVariables.ts");
        File.Exists(path).Should().BeTrue($"the frontend mirror must exist at {path}");

        var content = File.ReadAllText(path);
        var match = Regex.Match(
            content,
            @"export\s+const\s+RESERVED_POWERSHELL_VARIABLES\s*=\s*\[(?<body>[\s\S]*?)\]\s*as\s+const;",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue(
            "upstreamVariables.ts must export RESERVED_POWERSHELL_VARIABLES as a literal array");

        var frontend = Regex.Matches(match.Groups["body"].Value, @"'([^']+)'")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        frontend.Should().BeEquivalentTo(
            PowerShellReservedVariables.All,
            "src/nodepilot-ui/src/lib/upstreamVariables.ts mirrors " +
            "NodePilot.Core.Activities.PowerShellReservedVariables");

        content.Should().Contain($"RESERVED_POWERSHELL_PREFIX = '{PowerShellReservedVariables.InternalPrefix}'");
    }

    [Fact]
    public void Contract_CoversTheAutomaticsThatOnlyAppearWhileAScriptRuns()
    {
        // These are exactly the ones a `Get-Variable -Scope Local` snapshot taken before the user
        // script cannot see, which is why the list exists at all.
        var lateBound = new[] { "_", "PSItem", "foreach", "switch", "Matches", "Error", "LASTEXITCODE" };

        foreach (var name in lateBound)
            PowerShellReservedVariables.IsReserved(name).Should().BeTrue($"'{name}' materialises mid-script");
    }

    [Theory]
    [InlineData("__npReserved")]
    [InlineData("__npOut")]
    [InlineData("__NPANYTHING")]
    public void Contract_CoversTheWrappersOwnNamespace(string name)
        => PowerShellReservedVariables.IsReserved(name).Should().BeTrue();

    [Theory]
    [InlineData("hostName")]
    [InlineData("result")]
    [InlineData("np")]
    public void Contract_LeavesOrdinaryScriptVariablesAlone(string name)
        => PowerShellReservedVariables.IsReserved(name).Should().BeFalse();

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
