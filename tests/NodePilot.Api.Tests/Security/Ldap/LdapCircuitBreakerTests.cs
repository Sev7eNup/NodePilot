using FluentAssertions;
using NodePilot.Api.Security.Ldap;
using Xunit;

namespace NodePilot.Api.Tests.Security.Ldap;

public class LdapCircuitBreakerTests
{
    [Fact]
    public void StartsClosed_AcquireSucceeds()
    {
        var breaker = new LdapCircuitBreaker();
        breaker.CurrentState.Should().Be(LdapCircuitBreaker.State.Closed);
        breaker.TryAcquire().Should().BeTrue();
    }

    [Fact]
    public void NConsecutiveFailures_TripsToOpen()
    {
        var breaker = new LdapCircuitBreaker(failureThreshold: 3);

        for (var i = 0; i < 3; i++)
        {
            breaker.TryAcquire().Should().BeTrue();
            breaker.RecordFailure();
        }

        breaker.CurrentState.Should().Be(LdapCircuitBreaker.State.Open);
        breaker.TryAcquire().Should().BeFalse();
    }

    [Fact]
    public void SuccessResetsFailureCounter()
    {
        var breaker = new LdapCircuitBreaker(failureThreshold: 3);

        breaker.TryAcquire(); breaker.RecordFailure();
        breaker.TryAcquire(); breaker.RecordFailure();
        breaker.TryAcquire(); breaker.RecordSuccess();
        // Two more failures after the success: still under threshold (counter restarts).
        breaker.TryAcquire(); breaker.RecordFailure();
        breaker.TryAcquire(); breaker.RecordFailure();

        breaker.CurrentState.Should().Be(LdapCircuitBreaker.State.Closed);
    }

    [Fact]
    public void Open_AfterCooldown_TransitionsToHalfOpenOnNextAcquire()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var breaker = new LdapCircuitBreaker(failureThreshold: 1, openDuration: TimeSpan.FromSeconds(30), now: clock.Now);
        breaker.TryAcquire(); breaker.RecordFailure();
        breaker.CurrentState.Should().Be(LdapCircuitBreaker.State.Open);

        clock.Advance(TimeSpan.FromSeconds(31));
        breaker.TryAcquire().Should().BeTrue();
        breaker.CurrentState.Should().Be(LdapCircuitBreaker.State.HalfOpen);
    }

    [Fact]
    public void HalfOpen_SecondAcquireBlocked()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var breaker = new LdapCircuitBreaker(failureThreshold: 1, openDuration: TimeSpan.FromSeconds(30), now: clock.Now);
        breaker.TryAcquire(); breaker.RecordFailure();
        clock.Advance(TimeSpan.FromSeconds(31));

        breaker.TryAcquire().Should().BeTrue();   // probe slot
        breaker.TryAcquire().Should().BeFalse();  // concurrent caller blocked
    }

    [Fact]
    public void HalfOpen_ProbeSuccess_ClosesAndResetsCounter()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var breaker = new LdapCircuitBreaker(failureThreshold: 2, openDuration: TimeSpan.FromSeconds(30), now: clock.Now);
        breaker.TryAcquire(); breaker.RecordFailure();
        breaker.TryAcquire(); breaker.RecordFailure();
        clock.Advance(TimeSpan.FromSeconds(31));

        breaker.TryAcquire(); breaker.RecordSuccess();

        breaker.CurrentState.Should().Be(LdapCircuitBreaker.State.Closed);
        // Counter must be reset — one fresh failure must not immediately re-trip.
        breaker.TryAcquire(); breaker.RecordFailure();
        breaker.CurrentState.Should().Be(LdapCircuitBreaker.State.Closed);
    }

    [Fact]
    public void HalfOpen_ProbeFailure_ReopensImmediately()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var breaker = new LdapCircuitBreaker(failureThreshold: 1, openDuration: TimeSpan.FromSeconds(30), now: clock.Now);
        breaker.TryAcquire(); breaker.RecordFailure();
        clock.Advance(TimeSpan.FromSeconds(31));

        breaker.TryAcquire().Should().BeTrue();
        breaker.RecordFailure();

        breaker.CurrentState.Should().Be(LdapCircuitBreaker.State.Open);
        // Cooldown should restart from the failure moment — not still elapsed.
        breaker.TryAcquire().Should().BeFalse();
    }

    [Fact]
    public void Constructor_RejectsZeroOrNegativeThreshold()
    {
        Action act = () => new LdapCircuitBreaker(failureThreshold: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private sealed class MutableClock
    {
        private DateTimeOffset _t;
        public MutableClock(DateTimeOffset start) { _t = start; }
        public DateTimeOffset Now() => _t;
        public void Advance(TimeSpan delta) { _t = _t.Add(delta); }
    }
}
