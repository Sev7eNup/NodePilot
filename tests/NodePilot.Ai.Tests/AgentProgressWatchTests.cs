using NodePilot.Ai.Agents;
using Xunit;

namespace NodePilot.Ai.Tests;

public sealed class AgentProgressWatchTests
{
    [Fact]
    public void AlternatingOldStatesCannotResetProgressAndStopIsLatched()
    {
        var watch = new AgentProgressWatch();
        for (var i = 0; i < 40; i++) watch.Check((i % 2).ToString());
        Assert.True(watch.Stopped);
        watch.Check("new after stop");
        Assert.True(watch.Stopped);
    }

    [Fact]
    public void NewObservationsAllowSustainedInvestigation()
    {
        var watch = new AgentProgressWatch();
        for (var i = 0; i < 100; i++)
        {
            watch.Check(i.ToString());
            for (var recall = 0; recall < 10; recall++) watch.Check(i.ToString());
            Assert.False(watch.Stopped);
        }
    }
}
