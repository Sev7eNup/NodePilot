using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace NodePilot.Api.Tests.Architecture;

/// <summary>
/// The three npm manifests carry their own <c>version</c> field, independent from
/// <c>Directory.Build.props</c>. A drift between them means Electron's packaged
/// <c>NodePilot.exe</c> reports a different version than "Apps &amp; features" (Inno's
/// <c>AppVersion</c> comes from the build). <see cref="NodePilot.Cli.Tests"/> guards the
/// same class of drift for the CLI; this extends that check to the npm manifests.
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
