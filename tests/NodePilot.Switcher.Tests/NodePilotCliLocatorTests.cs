using FluentAssertions;
using NodePilot.Switcher.Configuration;
using Xunit;

namespace NodePilot.Switcher.Tests;

public sealed class NodePilotCliLocatorTests
{
    // A relative PATH entry would resolve against whatever working directory the elevated process has.
    [Fact]
    public void PathCandidates_WithRelativeEntries_SkipsThem()
    {
        var candidates = NodePilotCliLocator.PathCandidates(
            @"C:\tools;relative\dir;;.\local; C:\Program Files\NodePilot\tools\np ");

        candidates.Should().Equal(
            @"C:\tools\np.exe",
            @"C:\Program Files\NodePilot\tools\np\np.exe");
    }

    [Fact]
    public void PathCandidates_WithoutAPath_ReturnsNothing()
    {
        NodePilotCliLocator.PathCandidates(null).Should().BeEmpty();
    }
}
