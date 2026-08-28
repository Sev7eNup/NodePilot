using System.Text.RegularExpressions;
using FluentAssertions;
using NodePilot.Cli;
using Xunit;

namespace NodePilot.Cli.Tests;

/// <summary>
/// Confirms `np --version` matches the product version declared in Directory.Build.props,
/// the single source of truth for the version number.
/// </summary>
public sealed class CliVersionTests
{
    [Fact]
    public void Current_MatchesTheProductVersionInDirectoryBuildProps()
    {
        var props = File.ReadAllText(Path.Combine(FindRepoRoot(), "Directory.Build.props"));
        var declared = Regex.Match(props, @"<Version>\s*([^<\s]+)\s*</Version>");

        declared.Success.Should().BeTrue("Directory.Build.props is the single source of the product version");
        CliVersion.Current.Should().Be(declared.Groups[1].Value);
    }

    [Fact]
    public void Current_DropsTheSourceRevisionSuffix()
    {
        // The SDK stamps "<version>+<commit-sha>" as the informational version; that suffix is
        // noise in --version output.
        CliVersion.Current.Should().NotContain("+");
        CliVersion.Current.Should().MatchRegex(@"^\d+\.\d+\.\d+");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NodePilot.slnx")))
            directory = directory.Parent;
        if (directory is null)
            throw new InvalidOperationException($"Could not locate NodePilot.slnx walking up from {AppContext.BaseDirectory}");
        return directory.FullName;
    }
}
