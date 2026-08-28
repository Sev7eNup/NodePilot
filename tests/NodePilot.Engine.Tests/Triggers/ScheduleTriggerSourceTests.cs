using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Scheduler;
using NodePilot.Scheduler.Sources;
using Quartz;
using Xunit;

namespace NodePilot.Engine.Tests.Triggers;

/// <summary>
/// Pre-Quartz validation coverage for <see cref="ScheduleTriggerSource"/>. Every throw
/// in <c>StartAsync</c> happens before the scheduler factory is touched, so a Mock that
/// never resolves is enough — we don't want to spin up a real Quartz scheduler in unit
/// tests. The actual cron-fire integration is owned by the Quartz library itself.
/// </summary>
[Collection(ScheduleJobSlotCollection.Name)]
public class ScheduleTriggerSourceTests
{
    private static JsonElement Cfg(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection().Build();

    private static IConfiguration ConfigWith(params (string key, string val)[] entries)
    {
        var dict = entries.ToDictionary(e => e.key, e => (string?)e.val);
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static ISchedulerFactory NeverCalledFactory() => Mock.Of<ISchedulerFactory>();

    private static TriggerContext Ctx(string configJson) => new()
    {
        WorkflowId = Guid.NewGuid(),
        NodeId = "trg",
        Config = Cfg(configJson),
        OnFire = _ => Task.CompletedTask,
    };

    [Fact]
    public async Task StartAsync_Throws_WhenCronExpressionMissing()
    {
        var src = new ScheduleTriggerSource(
            NeverCalledFactory(),
            NullLogger<ScheduleTriggerSource>.Instance,
            EmptyConfig());

        var act = () => src.StartAsync(Ctx("""{}"""), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*'cronExpression' is required*");
    }

    [Fact]
    public async Task StartAsync_Throws_OnInvalidCronSyntax()
    {
        // "not a cron" can't be parsed by Quartz CronExpression — the source must surface
        // a clean InvalidOperationException with the original cron string for the operator.
        var src = new ScheduleTriggerSource(
            NeverCalledFactory(),
            NullLogger<ScheduleTriggerSource>.Instance,
            EmptyConfig());

        var act = () => src.StartAsync(Ctx("""{"cronExpression":"not a cron"}"""), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*invalid cron 'not a cron'*");
    }

    [Fact]
    public async Task StartAsync_Throws_WhenIntervalBelowMin()
    {
        // "* * * * * ?" fires every second. With the default min interval of 60s this must
        // be rejected — the operator-config-knob (Trigger:Schedule:MinIntervalSeconds)
        // exists exactly to prevent rogue cron strings from saturating the engine.
        var src = new ScheduleTriggerSource(
            NeverCalledFactory(),
            NullLogger<ScheduleTriggerSource>.Instance,
            EmptyConfig());

        var act = () => src.StartAsync(Ctx("""{"cronExpression":"* * * * * ?"}"""), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*below the configured minimum*");
    }

    [Fact]
    public async Task StartAsync_AllowsBelowMin_WhenMinSetToOne()
    {
        // MinIntervalSeconds <= 1 disables the min-interval guard. Useful for low-latency
        // environments. We can't fully StartAsync without a real Quartz scheduler, so we
        // expect a different failure mode (factory returning null/throwing) — what we're
        // pinning here is that the min-interval check itself does NOT throw.
        var src = new ScheduleTriggerSource(
            NeverCalledFactory(),
            NullLogger<ScheduleTriggerSource>.Instance,
            ConfigWith(("Trigger:Schedule:MinIntervalSeconds", "1")));

        try
        {
            // The factory returns null from Mock.Of, which surfaces a NullReferenceException
            // from inside Quartz. The point is: we got past the min-interval check.
            var act = () => src.StartAsync(Ctx("""{"cronExpression":"* * * * * ?"}"""), CancellationToken.None);

            // FluentAssertions' Where/predicate builds an expression tree which forbids 'is not'
            // pattern-matching, so use a method-call predicate instead.
            await act.Should().ThrowAsync<Exception>()
                .Where(ex => !IsBelowMinIntervalMessage(ex));
        }
        finally
        {
            // Getting past the min-interval check means we also got past the cap check, so
            // this source took an active-job slot before failing. Release it, or the
            // process-static counter stays inflated for every later test in the process.
            await src.DisposeAsync();
        }
    }

    private static bool IsBelowMinIntervalMessage(Exception ex)
        => ex.GetType() == typeof(InvalidOperationException)
           && ex.Message.Contains("below the configured minimum");

    [Fact]
    public async Task StartAsync_Throws_WhenMaxActiveJobsExceeded()
    {
        // The max-active-jobs counter is process-static. Set the cap to 0 so any single
        // call exceeds it, bypassing the need to spin up many sources. Cleanup: Dispose
        // each source so the static counter doesn't poison subsequent tests.
        var src = new ScheduleTriggerSource(
            NeverCalledFactory(),
            NullLogger<ScheduleTriggerSource>.Instance,
            ConfigWith(("Trigger:Schedule:MaxActiveJobs", "0")));

        try
        {
            var act = () => src.StartAsync(
                Ctx("""{"cronExpression":"0 0 * * * ?"}"""),
                CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*maximum number of active cron jobs (0)*");
        }
        finally
        {
            await src.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_IsSafe_WhenStartAsyncWasNeverCalled()
    {
        // No prior Start -> no JobKey set -> DisposeAsync must short-circuit without
        // touching the scheduler. Otherwise the static counter or Quartz interaction
        // would erroneously fire.
        var src = new ScheduleTriggerSource(
            NeverCalledFactory(),
            NullLogger<ScheduleTriggerSource>.Instance,
            EmptyConfig());

        await src.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_ReleasesTheJobSlotExactlyOnce_AcrossRepeatedCalls()
    {
        // Repeated disposal must release the process-wide active-job slot only once.
        // A negative counter would let subsequent sources bypass MaxActiveJobs.
        var scheduler = new Mock<IScheduler>();
        var factory = new Mock<ISchedulerFactory>();
        factory.Setup(f => f.GetScheduler(It.IsAny<CancellationToken>())).ReturnsAsync(scheduler.Object);
        factory.Setup(f => f.GetScheduler(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(scheduler.Object);

        var cap1 = ConfigWith(("Trigger:Schedule:MaxActiveJobs", "1"));
        var first = new ScheduleTriggerSource(
            factory.Object, NullLogger<ScheduleTriggerSource>.Instance, cap1);
        await first.StartAsync(Ctx("""{"cronExpression":"0 0 * * * ?"}"""), CancellationToken.None);

        // One real release, then two that must do nothing.
        await first.DisposeAsync();
        await first.DisposeAsync();
        await first.DisposeAsync();

        // One source fits after disposal; a second must still hit the configured cap.
        var fits = new ScheduleTriggerSource(
            factory.Object, NullLogger<ScheduleTriggerSource>.Instance, cap1);
        var overflows = new ScheduleTriggerSource(
            factory.Object, NullLogger<ScheduleTriggerSource>.Instance, cap1);
        try
        {
            await fits.StartAsync(Ctx("""{"cronExpression":"0 0 * * * ?"}"""), CancellationToken.None);

            var act = () => overflows.StartAsync(
                Ctx("""{"cronExpression":"0 0 * * * ?"}"""), CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*maximum number of active cron jobs (1)*");
        }
        finally
        {
            await fits.DisposeAsync();
            await overflows.DisposeAsync();
        }
    }

    // ------------------------------------------- missed fires are skipped, never replayed

    private static ISchedulerFactory WorkingFactory()
    {
        var factory = new Mock<ISchedulerFactory>();
        factory.Setup(f => f.GetScheduler(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IScheduler>());
        return factory.Object;
    }

    [Fact]
    public async Task StartAsync_WithStaleCursor_DeliversNothingAndFastForwardsIt()
    {
        // A restart used to replay every cron occurrence between the cursor and now: an hour of
        // downtime on a minute cadence meant sixty runs per workflow, before any live work.
        var stale = DateTimeOffset.UtcNow.AddHours(-1);
        var saved = new List<TriggerCheckpoint>();
        var delivered = 0;
        var ctx = new TriggerContext
        {
            WorkflowId = Guid.NewGuid(),
            NodeId = "trg",
            Config = Cfg("""{"cronExpression":"0 * * * * ?"}"""),
            OnFire = _ => Task.CompletedTask,
            OnDurableFire = _ => { delivered++; return Task.FromResult(true); },
            ReadCheckpoint = () => Task.FromResult<TriggerCheckpoint?>(
                new TriggerCheckpoint(stale.ToString("O"), "prev")),
            SaveCheckpoint = cp => { saved.Add(cp); return Task.FromResult(true); },
        };
        var src = new ScheduleTriggerSource(
            WorkingFactory(), NullLogger<ScheduleTriggerSource>.Instance, EmptyConfig());

        await src.StartAsync(ctx, CancellationToken.None);
        await src.DisposeAsync();

        delivered.Should().Be(0, "missed schedule ticks carry no data and are never replayed");
        saved.Should().ContainSingle();
        DateTimeOffset.Parse(saved[0].Position, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind)
            .Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1),
                "the cursor has to move past the skipped window, or the next start replays it");
    }

    [Fact]
    public async Task StartAsync_WhenCursorCannotBeAdvanced_DoesNotGoLive()
    {
        // Going live on a cursor still pointing into the past would deliver current fires while
        // the next start replays exactly the window this one skipped.
        var ctx = new TriggerContext
        {
            WorkflowId = Guid.NewGuid(),
            NodeId = "trg",
            Config = Cfg("""{"cronExpression":"0 * * * * ?"}"""),
            OnFire = _ => Task.CompletedTask,
            OnDurableFire = _ => Task.FromResult(true),
            ReadCheckpoint = () => Task.FromResult<TriggerCheckpoint?>(
                new TriggerCheckpoint(DateTimeOffset.UtcNow.AddHours(-1).ToString("O"), "prev")),
            SaveCheckpoint = _ => Task.FromResult(false),
        };
        var src = new ScheduleTriggerSource(
            WorkingFactory(), NullLogger<ScheduleTriggerSource>.Instance, EmptyConfig());

        var act = () => src.StartAsync(ctx, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*could not be advanced*");
        await src.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_WithoutCursor_SeedsWithoutDelivering()
    {
        var delivered = 0;
        TriggerCheckpoint? seeded = null;
        var ctx = new TriggerContext
        {
            WorkflowId = Guid.NewGuid(),
            NodeId = "trg",
            Config = Cfg("""{"cronExpression":"0 * * * * ?"}"""),
            OnFire = _ => Task.CompletedTask,
            OnDurableFire = _ => { delivered++; return Task.FromResult(true); },
            ReadCheckpoint = () => Task.FromResult<TriggerCheckpoint?>(null),
            InitializeCheckpoint = cp => { seeded = cp; return Task.FromResult(true); },
        };
        var src = new ScheduleTriggerSource(
            WorkingFactory(), NullLogger<ScheduleTriggerSource>.Instance, EmptyConfig());

        await src.StartAsync(ctx, CancellationToken.None);
        await src.DisposeAsync();

        delivered.Should().Be(0);
        seeded.Should().NotBeNull();
    }
}
