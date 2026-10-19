using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Hosting;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

public sealed class ServiceRestartCoordinatorTests
{
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopped = new();
        private readonly TaskCompletionSource _stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StopCalls { get; private set; }
        public Task StopRequested => _stopRequested.Task;

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            StopCalls++;
            _stopRequested.TrySetResult();
        }

        public void RaiseStopped() => _stopped.Cancel();
    }

    private static ServiceRestartCoordinator NewCoordinator(
        FakeLifetime lifetime, ServiceRestartCapability capability, List<int> exits)
        => new(lifetime, NullLogger<ServiceRestartCoordinator>.Instance, () => capability, TimeSpan.Zero, exits.Add);

    [Fact]
    public void TryScheduleRestart_CapabilityMissing_ReturnsFalseAndDoesNotStopHost()
    {
        var lifetime = new FakeLifetime();
        var exits = new List<int>();
        var sut = NewCoordinator(lifetime, ServiceRestartCapability.No("NOT_A_WINDOWS_SERVICE"), exits);

        sut.TryScheduleRestart("admin").Should().BeFalse();

        sut.IsRestartScheduled.Should().BeFalse();
        lifetime.StopCalls.Should().Be(0);
        exits.Should().BeEmpty();
    }

    [Fact]
    public async Task TryScheduleRestart_CapabilityAvailable_StopsHostThenExitsWithRestartCode()
    {
        var lifetime = new FakeLifetime();
        var exits = new List<int>();
        var sut = NewCoordinator(lifetime, ServiceRestartCapability.Yes, exits);

        sut.TryScheduleRestart("admin").Should().BeTrue();
        await lifetime.StopRequested.WaitAsync(TimeSpan.FromSeconds(5));

        exits.Should().BeEmpty("the process must only exit once the host has stopped");
        lifetime.RaiseStopped();

        exits.Should().Equal(ServiceRestartCoordinator.RestartExitCode);
    }

    [Fact]
    public async Task TryScheduleRestart_CalledTwice_SchedulesOnlyOnce()
    {
        var lifetime = new FakeLifetime();
        var exits = new List<int>();
        var sut = NewCoordinator(lifetime, ServiceRestartCapability.Yes, exits);

        sut.TryScheduleRestart("admin").Should().BeTrue();
        sut.TryScheduleRestart("admin").Should().BeTrue();
        await lifetime.StopRequested.WaitAsync(TimeSpan.FromSeconds(5));
        lifetime.RaiseStopped();

        lifetime.StopCalls.Should().Be(1);
        exits.Should().HaveCount(1);
        sut.IsRestartScheduled.Should().BeTrue();
    }

    [Fact]
    public void RestartExitCode_IsNonZero()
    {
        // Zero would read as a clean stop to the SCM and skip the recovery actions.
        ServiceRestartCoordinator.RestartExitCode.Should().NotBe(0);
    }
}
