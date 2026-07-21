using FluentAssertions;
using NodePilot.Core.Models;
using NodePilot.Remote;
using Xunit;

namespace NodePilot.Engine.Tests.Remote;

public class NoOpSessionFactoryTests
{
    [Fact]
    public async Task ExecuteScriptAsync_DefaultOptions_ReturnsSuccessImmediately()
    {
        var factory = new NoOpSessionFactory(new NoOpRemoteOptions());
        var machine = new ManagedMachine { Hostname = "fake" };

        await using var session = await factory.CreateSessionAsync(machine, credential: null, CancellationToken.None);
        var result = await session.ExecuteScriptAsync("whatever", timeoutSeconds: 60);

        result.Success.Should().BeTrue();
        result.ErrorOutput.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteScriptAsync_WithLatencyConfigured_DelaysAtLeastMinMs()
    {
        var factory = new NoOpSessionFactory(new NoOpRemoteOptions { MinLatencyMs = 50, MaxLatencyMs = 50 });
        var machine = new ManagedMachine { Hostname = "fake" };

        await using var session = await factory.CreateSessionAsync(machine, credential: null, CancellationToken.None);
        var start = DateTime.UtcNow;
        var result = await session.ExecuteScriptAsync("whatever");
        var elapsed = DateTime.UtcNow - start;

        result.Success.Should().BeTrue();
        elapsed.TotalMilliseconds.Should().BeGreaterThanOrEqualTo(45); // allow small jitter
    }

    [Fact]
    public async Task ExecuteScriptAsync_FullFailureRate_ReturnsFailure()
    {
        var factory = new NoOpSessionFactory(new NoOpRemoteOptions
        {
            SimulateFailures = true,
            FailureRate = 1.0
        });
        var machine = new ManagedMachine { Hostname = "fake" };

        await using var session = await factory.CreateSessionAsync(machine, credential: null, CancellationToken.None);
        var result = await session.ExecuteScriptAsync("whatever");

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("Simulated");
    }
}
