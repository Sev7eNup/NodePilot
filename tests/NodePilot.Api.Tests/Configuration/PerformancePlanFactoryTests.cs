using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NodePilot.Api.Configuration;
using Xunit;

namespace NodePilot.Api.Tests.Configuration;

public sealed class PerformancePlanFactoryTests
{
    private static IConfiguration Build(string? experimentalMode)
    {
        var values = new Dictionary<string, string?>();
        if (experimentalMode is not null) values["Engine:ExperimentalMode"] = experimentalMode;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Theory]
    [InlineData(null, false)]   // not configured: local scripts run as they did up to 1.5.4
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void Create_ReadsExperimentalMode_OffByDefault(string? configured, bool expected)
        => PerformancePlanFactory.Create(Build(configured)).ExperimentalMode.Should().Be(expected);

    [Fact]
    public void Describe_SaysWhetherTheScriptProcessLimitIsInForce()
    {
        // The startup line is what an operator reads to see what the process runs on. Without
        // experimental mode there is no limit, and a number there would claim one.
        PerformancePlanFactory.Describe(PerformancePlanFactory.Create(Build(null)))
            .Should().Contain("script processes not limited (experimental mode off)");

        var on = PerformancePlanFactory.Create(Build("true"));
        PerformancePlanFactory.Describe(on)
            .Should().Contain($"script processes {on.MaxScriptProcesses.Value} (").And.Contain("experimental mode)");
    }
}
