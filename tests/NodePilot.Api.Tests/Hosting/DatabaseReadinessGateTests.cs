using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task WaitForDatabaseAsync_ProbeThrows_LogsTheReasonWhileWaitingAndAtTimeout()
    {
        // Without the reason the log only said "waiting", and a TLS rejection was visible solely
        // in the database server's own log.
        var log = new NodePilot.TestCommons.CapturingLogger();
        const string reason = "connect failed (remote certificate was rejected)";
        await DatabaseReadinessGate.WaitForDatabaseAsync(
            canConnectAsync: _ => throw new InvalidOperationException("connect failed",
                new System.Security.Authentication.AuthenticationException("remote certificate was rejected")),
            timeout: TimeSpan.FromMilliseconds(200),
            pollInterval: TimeSpan.Zero,
            logger: log,
            delayAsync: (_, _) => Task.Delay(100));

        log.Entries.Should().Contain(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Information
            && e.Message.Contains(reason));
        log.Entries.Should().Contain(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error
            && e.Message.Contains(reason));
    }

    [Fact]
    public async Task OpenAndCloseAsync_RefusedConnection_ThrowsWithoutEfErrorLogging()
    {
        var efLog = new NodePilot.TestCommons.CapturingLogger();
        using var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(
            b => b.AddProvider(new SingleLoggerProvider(efLog)));
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "absent.db");
        await using var db = new NodePilot.Data.NodePilotDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<NodePilot.Data.NodePilotDbContext>()
                .UseSqlite($"Data Source={missing};Mode=ReadOnly")
                .UseLoggerFactory(loggerFactory).Options);

        Func<Task> probe = () => DatabaseReadinessGate.OpenAndCloseAsync(db, CancellationToken.None);

        await probe.Should().ThrowAsync<Exception>("the wait loop needs the cause");
        efLog.Entries.Should().NotContain(e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Error,
            "a database that is down at boot must not log an error per poll");
    }

    private sealed class SingleLoggerProvider(Microsoft.Extensions.Logging.ILogger logger)
        : Microsoft.Extensions.Logging.ILoggerProvider
    {
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => logger;
        public void Dispose() { }
    }

    [Fact]
    public void Program_RunsTheDatabaseBootInAHostedServiceNotBeforeRun()
    {
        // Code between Build() and RunAsync() runs before the Windows service reports running, and
        // the service control manager gives up after 30 seconds. The database wait lasts up to
        // Database:StartupWaitSeconds, so it has to stay in DatabaseBootService.StartingAsync.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "NodePilot.slnx"))) root = root.Parent;
        var program = File.ReadAllText(Path.Combine(root!.FullName, "src", "NodePilot.Api", "Program.cs"));

        program.Should().Contain("AddHostedService<NodePilot.Api.Hosting.DatabaseBootService>()");
        program.Should().NotContain("WaitForDatabaseAsync");
        program.Should().NotContain("MigrationBootstrapper.Bootstrap");
        typeof(DatabaseBootService).Should().Implement<Microsoft.Extensions.Hosting.IHostedLifecycleService>();
    }
}
