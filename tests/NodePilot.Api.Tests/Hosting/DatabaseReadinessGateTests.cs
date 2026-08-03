using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Hosting;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

public sealed class DatabaseReadinessGateTests
{
    private static IConfiguration ConfigWith(string? startupWaitSeconds)
    {
        var values = new Dictionary<string, string?>();
        if (startupWaitSeconds is not null)
            values[DatabaseReadinessGate.StartupWaitSecondsKey] = startupWaitSeconds;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void ResolveStartupWait_KeyAbsent_ReturnsDefault()
        => DatabaseReadinessGate.ResolveStartupWait(ConfigWith(null))
            .Should().Be(DatabaseReadinessGate.DefaultStartupWait);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("soon")]
    [InlineData("120.5")]
    public void ResolveStartupWait_UnusableValue_ReturnsDefault(string raw)
        => DatabaseReadinessGate.ResolveStartupWait(ConfigWith(raw))
            .Should().Be(DatabaseReadinessGate.DefaultStartupWait);

    [Theory]
    [InlineData("300", 300)]
    [InlineData(" 45 ", 45)]
    public void ResolveStartupWait_ExplicitValue_IsHonoured(string raw, int expectedSeconds)
        => DatabaseReadinessGate.ResolveStartupWait(ConfigWith(raw))
            .Should().Be(TimeSpan.FromSeconds(expectedSeconds));

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void ResolveStartupWait_ZeroOrNegative_OptsOutOfWaiting(string raw)
        => DatabaseReadinessGate.ResolveStartupWait(ConfigWith(raw))
            .Should().Be(TimeSpan.Zero);

    [Fact]
    public void ResolveStartupWait_AboveCap_IsClamped()
    {
        // 86400 is the "thought the unit was something else" typo. Honouring it would hang
        // service start for a day with no diagnosis.
        DatabaseReadinessGate.ResolveStartupWait(ConfigWith("86400"))
            .Should().Be(DatabaseReadinessGate.MaxStartupWait);
    }

    [Fact]
    public async Task WaitForDatabaseAsync_ZeroTimeout_ProbesOnceAndProceeds()
    {
        // The documented opt-out: Database:StartupWaitSeconds=0 must still probe (so a ready
        // database is reported as ready) but must never sleep.
        var attempts = 0;
        var delays = 0;

        var result = await DatabaseReadinessGate.WaitForDatabaseAsync(
            canConnectAsync: _ => { attempts++; return Task.FromResult(false); },
            timeout: TimeSpan.Zero,
            pollInterval: TimeSpan.FromSeconds(2),
            logger: NullLogger.Instance,
            delayAsync: (_, _) => { delays++; return Task.CompletedTask; });

        result.Should().BeFalse();
        attempts.Should().Be(1);
        delays.Should().Be(0);
    }

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
