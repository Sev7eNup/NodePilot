using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Data.Availability;
using Xunit;

namespace NodePilot.Data.Tests.Availability;

/// <summary>
/// Covers the breaker's state machine. The load-bearing rule under test throughout is the
/// single-writer invariant: after boot, only the probe may publish
/// <see cref="DatabaseAvailabilityState.Available"/>, and interceptors may only degrade.
/// </summary>
public sealed class DatabaseAvailabilityTrackerTests
{
    private static DatabaseAvailabilityTracker Tracker(int successesToRecover = 2, int failuresToOpen = 2)
        => new(NullLogger<DatabaseAvailabilityTracker>.Instance, successesToRecover, failuresToOpen);

    private static DatabaseAvailabilityTracker Booted(int successesToRecover = 2, int failuresToOpen = 2)
    {
        var t = Tracker(successesToRecover, failuresToOpen);
        t.MarkBootComplete();
        return t;
    }

    [Fact]
    public void State_BeforeBootCompletes_IsBootingAndServable()
    {
        var tracker = Tracker();

        tracker.State.Should().Be(DatabaseAvailabilityState.Booting);
        // The boot block itself needs the database and there is no HTTP pipeline yet, so there is
        // nothing to seal off.
        tracker.IsServable.Should().BeTrue();
    }

    [Fact]
    public void ReportUnreachable_WhileBooting_IsInert()
    {
        // DatabaseReadinessGate polls a database that is routinely late at boot. If those failed
        // probes opened the breaker, the migration that follows would run with retries disabled.
        var tracker = Tracker();

        tracker.ReportUnreachable(DatabaseOutageReason.Unreachable);

        tracker.State.Should().Be(DatabaseAvailabilityState.Booting);
        tracker.CurrentOutage.Should().BeNull();
    }

    [Fact]
    public void Arm_WhileBooting_IsInert()
    {
        var tracker = Tracker();

        tracker.Arm();

        tracker.State.Should().Be(DatabaseAvailabilityState.Booting);
    }

    [Fact]
    public void ReportUnreachable_FirstConnectionFailure_OpensImmediately()
    {
        // No counter, no hysteresis: opening a connection is identical for every caller, so one
        // failed physical open is enough evidence.
        var tracker = Booted();

        tracker.ReportUnreachable(DatabaseOutageReason.Unreachable);

        tracker.State.Should().Be(DatabaseAvailabilityState.Unavailable);
        tracker.IsServable.Should().BeFalse();
        tracker.CurrentOutage!.Reason.Should().Be(DatabaseOutageReason.Unreachable);
    }

    [Fact]
    public void Arm_CommandTimeout_DoesNotOpenByItself()
    {
        // The false positive this whole design exists to avoid: one slow query must not put a
        // "database unavailable" banner in front of every user.
        var tracker = Booted();

        tracker.Arm();

        tracker.State.Should().Be(DatabaseAvailabilityState.Armed);
        tracker.IsServable.Should().BeTrue();
        tracker.CurrentOutage.Should().BeNull();
    }

    [Fact]
    public async Task Arm_WakesTheProbe()
    {
        // Arming is only useful if the adjudicator actually runs; otherwise Armed is a state
        // nothing
        // ever leaves.
        var tracker = Booted();
        var waiting = tracker.WaitForProbeRequestAsync(TestContext.Current.CancellationToken);

        tracker.Arm();

        await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        waiting.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Arm_BeforeProbeStartsWaiting_IsNotLost()
    {
        // Probe requests are level-triggered, not a pulse: an interceptor can arm while the probe
        // is
        // executing SELECT 1 and before it installs the next waiter. The subsequent wait must
        // consume
        // that already-published request immediately rather than sleeping for the full idle
        // interval.
        var tracker = Booted();

        tracker.Arm();
        var waiting = tracker.WaitForProbeRequestAsync(TestContext.Current.CancellationToken);

        await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        waiting.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task ProbeRequest_PublishedBeforeWait_IsConsumedExactlyOnce()
    {
        var tracker = Booted();
        tracker.Arm();

        await tracker.WaitForProbeRequestAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        using var cancellation = new CancellationTokenSource();
        var secondWait = tracker.WaitForProbeRequestAsync(cancellation.Token);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        secondWait.IsCompleted.Should().BeFalse(
            "one request must wake one probe iteration; retaining the consumed signal causes a hot loop");

        await cancellation.CancelAsync();
        await secondWait.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void ReportProbeFailed_Wedged_WaitsForTheThreshold()
    {
        var tracker = Booted(failuresToOpen: 2);
        tracker.Arm();

        tracker.ReportProbeFailed(DatabaseOutageReason.Wedged);
        tracker.State.Should().Be(DatabaseAvailabilityState.Armed, "one ambiguous answer is not proof");

        tracker.ReportProbeFailed(DatabaseOutageReason.Wedged);
        tracker.State.Should().Be(DatabaseAvailabilityState.Unavailable);
        tracker.CurrentOutage!.Reason.Should().Be(DatabaseOutageReason.Wedged);
    }

    [Fact]
    public void ReportProbeFailed_ConnectionClass_OpensOnTheFirstObservation()
    {
        // The probe holds its own dedicated connection, so a connection-class answer there has no
        // pool handout to misread and needs no second opinion.
        var tracker = Booted(failuresToOpen: 2);

        tracker.ReportProbeFailed(DatabaseOutageReason.Unreachable);

        tracker.State.Should().Be(DatabaseAvailabilityState.Unavailable);
    }

    [Fact]
    public void ReportProbeFailed_RejectedByServer_OpensAndKeepsTheReason()
    {
        // A wrong password must never be presented as "reconnecting, please wait" - the reason is
        // the
        // only thing that tells the operator to go and fix something.
        var tracker = Booted();

        tracker.ReportProbeFailed(DatabaseOutageReason.RejectedByServer);

        tracker.State.Should().Be(DatabaseAvailabilityState.Unavailable);
        tracker.CurrentOutage!.Reason.Should().Be(DatabaseOutageReason.RejectedByServer);
    }

    [Fact]
    public void ReportUnreachable_CauseChangesMidOutage_UpdatesTheReasonWithoutReopening()
    {
        // "Unreachable" turning into "rejected by server" is the difference between waiting and
        // fixing a password, and it must survive without restarting the outage clock.
        var tracker = Booted();
        tracker.ReportUnreachable(DatabaseOutageReason.Unreachable);
        var openedAt = tracker.CurrentOutage!.SinceUtc;

        tracker.ReportUnreachable(DatabaseOutageReason.RejectedByServer);

        tracker.CurrentOutage!.Reason.Should().Be(DatabaseOutageReason.RejectedByServer);
        tracker.CurrentOutage!.SinceUtc.Should().Be(openedAt);
    }

    [Fact]
    public void ReportProbeFailed_CauseChangesMidOutage_UpdatesTheReason()
    {
        var tracker = Booted();
        tracker.ReportUnreachable(DatabaseOutageReason.Unreachable);

        tracker.ReportProbeFailed(DatabaseOutageReason.Wedged);

        tracker.CurrentOutage!.Reason.Should().Be(DatabaseOutageReason.Wedged);
    }

    [Fact]
    public void RejectedByServer_IsStickyForTheRestOfTheOutage()
    {
        var tracker = Booted();
        tracker.ReportUnreachable(DatabaseOutageReason.RejectedByServer);

        tracker.ReportProbeFailed(DatabaseOutageReason.Unreachable);
        tracker.ReportUnreachable(DatabaseOutageReason.Wedged);

        tracker.CurrentOutage!.Reason.Should().Be(DatabaseOutageReason.RejectedByServer);
    }

    [Fact]
    public void ReportProbeSucceeded_SingleSuccess_StaysUnavailableUntilThreshold()
    {
        // Anti-flap: a server that answers once while it is still restarting would otherwise take
        // the
        // whole installation back into a hammering loop.
        var tracker = Booted(successesToRecover: 2);
        tracker.ReportUnreachable(DatabaseOutageReason.Unreachable);

        tracker.ReportProbeSucceeded();
        tracker.State.Should().Be(DatabaseAvailabilityState.Unavailable);

        tracker.ReportProbeSucceeded();
        tracker.State.Should().Be(DatabaseAvailabilityState.Available);
        tracker.CurrentOutage.Should().BeNull();
    }

    [Fact]
    public void ReportProbeSucceeded_FailureInBetween_RestartsTheRecoveryCount()
    {
        var tracker = Booted(successesToRecover: 2);
        tracker.ReportUnreachable(DatabaseOutageReason.Unreachable);

        tracker.ReportProbeSucceeded();
        tracker.ReportProbeFailed(DatabaseOutageReason.Unreachable);
        tracker.ReportProbeSucceeded();

        tracker.State.Should().Be(DatabaseAvailabilityState.Unavailable, "the streak was broken");
    }

    [Fact]
    public void OutageEpisodeId_IsMonotonicAndRecoveryPayloadSurvivesImmediateFlap()
    {
        var tracker = Booted(successesToRecover: 1);
        tracker.ReportUnreachable(DatabaseOutageReason.Unreachable);
        var first = tracker.Snapshot;

        tracker.ReportUnreachable(DatabaseOutageReason.Wedged);
        tracker.Snapshot.OutageEpisodeId.Should().Be(first.OutageEpisodeId,
            "reason updates remain part of the same actual outage episode");

        var recovered = new List<long>();
        tracker.OutageRecovered += recovered.Add;
        var flapOpened = false;
        tracker.StateChanged += state =>
        {
            if (state is not DatabaseAvailabilityState.Available || flapOpened) return;
            flapOpened = true;
            tracker.ReportUnreachable(DatabaseOutageReason.Unreachable);
        };

        tracker.ReportProbeSucceeded(first.OutageEpisodeId);

        var second = tracker.Snapshot;
        second.State.Should().Be(DatabaseAvailabilityState.Unavailable);
        second.CurrentOutage.Should().NotBeNull();
        second.CurrentOutage!.EpisodeId.Should().Be(second.OutageEpisodeId);
        second.OutageEpisodeId.Should().Be(first.OutageEpisodeId + 1);
        recovered.Should().ContainSingle(
            "one actual episode recovered before the immediate flap");
        recovered[0].Should().Be(first.OutageEpisodeId,
            "the recovery event must carry the recovered episode, not a newer snapshot read after a flap");
    }

    [Fact]
    public async Task WaitUntilServableAsync_WhileUnavailable_DoesNotCompleteUntilRecovery()
    {
        var tracker = Booted(successesToRecover: 1);
        tracker.ReportUnreachable(DatabaseOutageReason.Unreachable);

        var waiting = tracker.WaitUntilServableAsync(TestContext.Current.CancellationToken);
        waiting.IsCompleted.Should().BeFalse();

        tracker.ReportProbeSucceeded();

        (await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
            .Should().BeTrue();
    }

    [Fact]
    public async Task WaitUntilServableAsync_TokenCancelled_ReturnsFalseAndDoesNotThrow()
    {
        // Every hosted-service gate is written as `if (!await ...) break;`, and
        // BackgroundServiceExceptionBehavior is left at its default StopHost, so an escaping
        // OperationCanceledException would take the host down on every shutdown.
        var tracker = Booted();
        tracker.ReportUnreachable(DatabaseOutageReason.Unreachable);
        using var cts = new CancellationTokenSource();

        var waiting = tracker.WaitUntilServableAsync(cts.Token);
        await cts.CancelAsync();

        (await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
            .Should().BeFalse();
    }

    [Fact]
    public async Task WaitUntilServableAsync_WhileArmed_CompletesImmediately()
    {
        // Armed is servable on purpose: parking every background service on a single slow query
        // would
        // be a self-inflicted outage.
        var tracker = Booted();
        tracker.Arm();

        (await tracker.WaitUntilServableAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public void StateChanged_RaisedOncePerTransition_NotPerReport()
    {
        // This is what turns "an ERROR every 5 seconds for the whole outage" into two log lines.
        var tracker = Booted();
        var states = new List<DatabaseAvailabilityState>();
        tracker.StateChanged += s => states.Add(s);

        tracker.ReportUnreachable(DatabaseOutageReason.Unreachable);
        tracker.ReportUnreachable(DatabaseOutageReason.Unreachable);
        tracker.ReportUnreachable(DatabaseOutageReason.Unreachable);

        states.Should().Equal(DatabaseAvailabilityState.Unavailable);
    }

    [Fact]
    public void StateChanged_SubscriberThrows_DoesNotBreakTheBreaker()
    {
        // The event runs on the thread of whatever query just failed, which may be a hosted-service
        // loop with StopHost semantics.
        var tracker = Booted();
        tracker.StateChanged += _ => throw new InvalidOperationException("subscriber is broken");

        var act = () => tracker.ReportUnreachable(DatabaseOutageReason.Unreachable);

        act.Should().NotThrow();
        tracker.State.Should().Be(DatabaseAvailabilityState.Unavailable);
    }

    [Fact]
    public void MarkBootComplete_CalledTwice_DoesNotResurrectFromAnOutage()
    {
        // Booting is terminal-in-reverse: nothing may ever re-enter it, and a stray second call
        // must
        // not hand an interceptor a way to publish Available.
        var tracker = Booted();
        tracker.ReportUnreachable(DatabaseOutageReason.Unreachable);

        tracker.MarkBootComplete();

        tracker.State.Should().Be(DatabaseAvailabilityState.Unavailable);
    }
}
