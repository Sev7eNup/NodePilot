using FluentAssertions;
using NodePilot.EngineSwitcher.Models;
using NodePilot.EngineSwitcher.Services;
using Xunit;

namespace NodePilot.EngineSwitcher.Tests;

public sealed class EnvironmentStateEvaluatorTests
{
    [Theory]
    [InlineData((int)ServiceRuntimeState.Running, (int)ServiceRuntimeState.Stopped, (int)EnvironmentState.NodePilotActive)]
    [InlineData((int)ServiceRuntimeState.Stopped, (int)ServiceRuntimeState.Running, (int)EnvironmentState.SystemCenterActive)]
    [InlineData((int)ServiceRuntimeState.Stopped, (int)ServiceRuntimeState.Stopped, (int)EnvironmentState.BothStopped)]
    [InlineData((int)ServiceRuntimeState.Running, (int)ServiceRuntimeState.Running, (int)EnvironmentState.Conflict)]
    public void Assess_ClassifiesExclusiveAndConflictStates(
        int nodePilotValue,
        int scorchValue,
        int expectedValue)
    {
        var nodePilot = (ServiceRuntimeState)nodePilotValue;
        var scorch = (ServiceRuntimeState)scorchValue;
        var expected = (EnvironmentState)expectedValue;
        var snapshot = new ManagedEnvironmentSnapshot(
            TestServices.Service("NodePilot", nodePilot),
            [TestServices.Service("orunbook", scorch)]);

        EnvironmentStateEvaluator.Assess(snapshot).Should().Be(expected);
    }

    [Fact]
    public void Assess_ClassifiesPartiallyRunningSystemCenterGroup()
    {
        var snapshot = new ManagedEnvironmentSnapshot(
            TestServices.Service("NodePilot"),
            [TestServices.Service("omanagement", ServiceRuntimeState.Running), TestServices.Service("orunbook")]);

        EnvironmentStateEvaluator.Assess(snapshot).Should().Be(EnvironmentState.SystemCenterPartial);
    }
}
