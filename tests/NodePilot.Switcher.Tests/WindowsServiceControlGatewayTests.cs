using FluentAssertions;
using NodePilot.Switcher.Services;
using Xunit;

namespace NodePilot.Switcher.Tests;

public sealed class WindowsServiceControlGatewayTests
{
    // The SCM answers 1061 while a service is start- or stop-pending; the stop must then still wait
    // and fall back to the forced stop instead of failing.
    [Theory]
    [InlineData(1061u, true)]
    [InlineData(1062u, true)]
    [InlineData(5u, false)]
    [InlineData(1051u, false)]
    [InlineData(1052u, false)]
    public void IsStopAlreadyUnderway_ForStopControlError_ReturnsWhetherTheWaitApplies(uint error, bool expected)
    {
        WindowsServiceControlGateway.IsStopAlreadyUnderway(error).Should().Be(expected);
    }
}
