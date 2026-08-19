using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace NodePilot.Api.Tests.Architecture;

/// <summary>
/// The three npm manifests carry their own <c>version</c> field, and nothing kept them in step
/// with <c>Directory.Build.props</c>. They sat at 1.2.5 while the product shipped 1.2.10 — so
/// "Apps &amp; features" reported 1.2.10 (the Inno <c>AppVersion</c> comes from the build) while the
/// file properties of the packaged <c>NodePilot.exe</c> reported 1.2.5, because Electron takes its
/// version from <c>package.json</c>.
///
/// This is the same defect <see cref="NodePilot.Cli.Tests"/> already guards for the CLI, whose own
/// docblock records that it "still said 1.0.0 after the product version had moved on". Three more
/// copies of a version number are three more chances to forget one, so they get the same treatment
/// rather than a convention nobody can enforce.
/// </summary>
public sealed class PackageVersionParityTests
{
    public static TheoryData<string> NpmManifests() => new()
    {
        Path.Combine("src", "nodepilot-ui", "package.json"),
        Path.Combine("src", "nodepilot-desktop", "package.json"),
        Path.Combine("src", "nodepilot-docs-ui", "package.json"),
    };

    [Theory]
    [MemberData(nameof(NpmManifests))]
    public void NpmManifest_DeclaresTheProductVersion(string relativePath)
    {
        var root = FindRepoRoot();
        var manifestPath = Path.Combine(root, relativePath);
        File.Exists(manifestPath).Should().BeTrue($"{relativePath} is part of the build");

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        manifest.RootElement.TryGetProperty("version", out var version).Should().BeTrue(
            $"{relativePath} must declare a version");

        version.GetString().Should().Be(
            ProductVersion(root),
            $"{relativePath} must match Directory.Build.props — bump all four together at release time");
    }

    private static string ProductVersion(string root)
    {
        var props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        var declared = Regex.Match(props, @"<Version>\s*([^<\s]+)\s*</Version>");
        declared.Success.Should().BeTrue("Directory.Build.props is the single source of the product version");
        return declared.Groups[1].Value;
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
