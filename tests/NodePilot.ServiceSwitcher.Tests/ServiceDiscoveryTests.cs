using FluentAssertions;
using NodePilot.ServiceSwitcher.Services;
using Xunit;

namespace NodePilot.ServiceSwitcher.Tests;

public sealed class ServiceDiscoveryTests
{
    [Fact]
    public void Discover_UsesValidatedNodePilotCandidateAndExactSystemCenterAllowlist()
    {
        var gateway = new FakeServiceControlGateway();
        gateway.Services["CustomNodePilot"] = TestServices.Service("CustomNodePilot") with
        {
            BinaryPath = @"C:\NodePilot\NodePilot.Api.exe --contentRoot C:\NodePilot",
        };
        gateway.Services["omanagement"] = TestServices.Service("omanagement");
        gateway.Services["orunbook"] = TestServices.Service("orunbook");
        gateway.Services["UsoSvc"] = TestServices.Service("UsoSvc");

        var result = new ServiceDiscovery(gateway, () => ["CustomNodePilot"]).Discover();

        result.NodePilot!.Name.Should().Be("CustomNodePilot");
        result.SystemCenterServices.Select(service => service.Name)
            .Should().Equal("omanagement", "orunbook");
        result.SystemCenterServices.Should().NotContain(service => service.Name == "UsoSvc");
    }

    [Fact]
    public void Discover_RejectsCandidateWhoseServiceDoesNotRunNodePilotApi()
    {
        var gateway = new FakeServiceControlGateway();
        gateway.Services["NodePilot"] = TestServices.Service("NodePilot") with
        {
            BinaryPath = @"C:\Windows\System32\svchost.exe",
        };

        new ServiceDiscovery(gateway, () => ["NodePilot"]).Discover().NodePilot.Should().BeNull();
    }

    [Fact]
    public void IsNodePilotBinary_RejectsExecutableThatOnlyContainsExpectedName()
    {
        ServiceDiscovery.IsNodePilotBinary(@"C:\Malware\evil-NodePilot.Api.exe")
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("NodePilot.Api.exe")]
    [InlineData("\"C:\\Program Files\\NodePilot\\NodePilot.Api.exe\" --contentRoot C:\\Data")]
    public void IsNodePilotBinary_AcceptsExpectedExecutable(string binaryPath) =>
        ServiceDiscovery.IsNodePilotBinary(binaryPath).Should().BeTrue();
}
