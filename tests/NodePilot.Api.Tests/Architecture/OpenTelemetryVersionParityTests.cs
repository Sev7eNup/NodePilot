using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace NodePilot.Api.Tests.Architecture;

/// <summary>
/// Dependabot's minor/patch group moves only the stable OpenTelemetry packages. These checks
/// fail when the prereleases are left behind, which the scrape tests do not reliably catch.
/// </summary>
public sealed class OpenTelemetryVersionParityTests
{
    private const string Core = "OpenTelemetry";
    private const string PrometheusExporter = "OpenTelemetry.Exporter.Prometheus.AspNetCore";

    [Fact]
    public void OpenTelemetryPackages_ShareOneMajorMinorRelease()
    {
        var versions = OpenTelemetryVersions();
        versions.Should().ContainKeys(Core, PrometheusExporter);

        var coreRelease = MajorMinor(versions[Core]);
        foreach (var (package, version) in versions)
            MajorMinor(version).Should().Be(coreRelease,
                $"{package} must track the same OpenTelemetry release as the core package");
    }

    [Fact]
    public void PrometheusExporter_MatchesTheCoreVersionExactly()
    {
        var versions = OpenTelemetryVersions();

        StripPrerelease(versions[PrometheusExporter]).Should().Be(versions[Core],
            "the Prometheus exporter binds to internals of its own core release");
    }

    private static Dictionary<string, string> OpenTelemetryVersions()
    {
        var props = File.ReadAllText(Path.Combine(FindRepoRoot(), "Directory.Packages.props"));
        return Regex.Matches(props, @"<PackageVersion\s+Include=""(OpenTelemetry(?:\.[^""]+)?)""\s+Version=""([^""]+)""")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
    }

    private static string StripPrerelease(string version) => version.Split('-')[0];

    private static string MajorMinor(string version)
    {
        var parts = StripPrerelease(version).Split('.');
        return $"{parts[0]}.{parts[1]}";
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
