using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Hosting;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

public sealed class DatabaseReadinessGateTests
{
    [Fact]
    public async Task WaitForDatabaseAsync_ConnectsImmediately_ReturnsTrueWithoutDelaying()
    {
        var delays = 0;
        var result = await DatabaseReadinessGate.WaitForDatabaseAsync(
            canConnectAsync: _ => Task.FromResult(true),
            timeout: TimeSpan.FromSeconds(120),
            pollInterval: TimeSpan.FromSeconds(2),
            logger: NullLogger.Instance,
            delayAsync: (_, _) => { delays++; return Task.CompletedTask; });

        result.Should().BeTrue();
        delays.Should().Be(0);
    }

    [Fact]
    public async Task WaitForDatabaseAsync_ConnectsAfterRetries_ReturnsTrue()
    {
        var attempts = 0;
        var result = await DatabaseReadinessGate.WaitForDatabaseAsync(
            canConnectAsync: _ => Task.FromResult(++attempts >= 3),
            timeout: TimeSpan.FromSeconds(120),
            pollInterval: TimeSpan.FromMilliseconds(1),
            logger: NullLogger.Instance,
            delayAsync: (_, _) => Task.CompletedTask);

        result.Should().BeTrue();
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task WaitForDatabaseAsync_ProbeThrows_TreatedAsNotReadyThenTimesOut()
    {
        var result = await DatabaseReadinessGate.WaitForDatabaseAsync(
            canConnectAsync: _ => throw new InvalidOperationException("db not up yet"),
            timeout: TimeSpan.Zero,
            pollInterval: TimeSpan.FromMilliseconds(1),
            logger: NullLogger.Instance,
            delayAsync: (_, _) => Task.CompletedTask);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task WaitForDatabaseAsync_CancellationRequested_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => DatabaseReadinessGate.WaitForDatabaseAsync(
            canConnectAsync: ct => Task.FromCanceled<bool>(ct),
            timeout: TimeSpan.FromSeconds(120),
            pollInterval: TimeSpan.FromMilliseconds(1),
            logger: NullLogger.Instance,
            delayAsync: (_, _) => Task.CompletedTask,
            ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
