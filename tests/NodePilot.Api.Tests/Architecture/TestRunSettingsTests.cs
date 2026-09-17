using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace NodePilot.Api.Tests.Architecture;

/// <summary>
/// Guards <c>coverage.runsettings</c>, which caps how much of the CI agent the test run takes.
///
/// <para>Both caps exist because the four-vCPU runner drops test processes under load: xunit.v3
/// launches every assembly as its own process and gives it 60 seconds to answer, and the
/// timing-sensitive suites lose their timeouts when the thread pool is starved. Removing either
/// number brings those failures back, and the file's comment is the only place the reasoning
/// lives — so the assembly count it names is pinned to the solution here rather than left to
/// rot after the next project is added.</para>
/// </summary>
public sealed class TestRunSettingsTests
{
    private static readonly Regex AssemblyCountInComment = new(@"the (\w+) assemblies run", RegexOptions.Compiled);

    private static readonly string[] NumberWords =
        ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"];

    [Fact]
    public void RunSettings_CapBothTestProcessesAndThreadsPerProcess()
    {
        var settings = XDocument.Load(Path.Combine(FindRepoRoot(), "coverage.runsettings"));

        settings.Root!.Element("RunConfiguration")!.Element("MaxCpuCount")!.Value.Should().Be("2");
        settings.Root.Element("xUnit")!.Element("maxParallelThreads")!.Value.Should().Be("2");
    }

    [Fact]
    public void RunSettings_NameTheNumberOfTestAssembliesTheSolutionActuallyRuns()
    {
        var repoRoot = FindRepoRoot();
        var solution = XDocument.Load(Path.Combine(repoRoot, "NodePilot.slnx"));
        var testProjects = solution.Descendants("Project")
            .Select(project => project.Attribute("Path")?.Value ?? "")
            .Count(path => path.EndsWith(".Tests.csproj", StringComparison.OrdinalIgnoreCase)
                && File.ReadAllText(Path.Combine(repoRoot, path.Replace('\\', Path.DirectorySeparatorChar)))
                    .Contains("Microsoft.NET.Test.Sdk", StringComparison.Ordinal));

        var comment = File.ReadAllText(Path.Combine(repoRoot, "coverage.runsettings"));
        var named = AssemblyCountInComment.Match(comment);

        named.Success.Should().BeTrue("coverage.runsettings explains its caps with the assembly count");
        named.Groups[1].Value.Should().Be(
            NumberWords[testProjects],
            "the solution runs {0} test assemblies, so the comment must say so",
            testProjects);
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
