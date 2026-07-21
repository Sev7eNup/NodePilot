using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodePilot.Api.Security.Ldap;
using Xunit;
using NodePilot.TestCommons;

namespace NodePilot.Api.Tests.Security.Ldap;

public class LdapAuthenticatorTests
{
    [Fact]
    public async Task Disabled_ReturnsUnavailable_WithoutCallingAdapter()
    {
        var adapter = new FakeAdapter();
        var auth = NewAuthenticator(new LdapOptions { Enabled = false }, adapter);

        var outcome = await auth.AuthenticateAsync("alice", "pw", default);

        outcome.Outcome.Should().Be(LdapAuthOutcome.Unavailable);
        outcome.UnavailableReason.Should().Be("ldap_disabled");
        adapter.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Success_ReturnsResult_AndKeepsCircuitClosed()
    {
        var canned = new LdapAuthResult("guid-123", "alice@firma.de", "Alice Example", new[] { "S-1-5-21-1" });
        var adapter = new FakeAdapter { Result = canned };
        var breaker = new LdapCircuitBreaker(failureThreshold: 2);
        var auth = NewAuthenticator(EnabledOptions(), adapter, breaker);

        var outcome = await auth.AuthenticateAsync("alice", "pw", default);

        outcome.Outcome.Should().Be(LdapAuthOutcome.Success);
        outcome.Result.Should().BeSameAs(canned);
        breaker.CurrentState.Should().Be(LdapCircuitBreaker.State.Closed);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task EmptyPassword_ReturnsInvalidCredentials_WithoutBinding(string? password)
    {
        // Regression: a zero-length password is an RFC 4513 unauthenticated bind that AD
        // accepts with LDAP_SUCCESS. It must be rejected as invalid credentials BEFORE the
        // adapter's Bind is ever reached — otherwise "knowing a username" bypasses the password.
        // Whitespace-only is folded in as belt-and-suspenders.
        var adapter = new FakeAdapter { Result = Sample("alice@firma.de") };
        var breaker = new LdapCircuitBreaker(failureThreshold: 2);
        var auth = NewAuthenticator(EnabledOptions(), adapter, breaker);

        var outcome = await auth.AuthenticateAsync("alice", password!, default);

        outcome.Outcome.Should().Be(LdapAuthOutcome.InvalidCredentials);
        outcome.Result.Should().BeNull();
        adapter.Calls.Should().Be(0);
        // InvalidCredentials (not Unavailable) so the AuthController never falls through to
        // the local-password path for an empty password either.
        breaker.CurrentState.Should().Be(LdapCircuitBreaker.State.Closed);
    }

    [Fact]
    public async Task UpnNormalization_PassesUpnToAdapter()
    {
        var adapter = new FakeAdapter { Result = Sample("alice@firma.de") };
        var auth = NewAuthenticator(EnabledOptions(), adapter);

        await auth.AuthenticateAsync(@"FIRMA\Alice", "pw", default);

        adapter.LastUpn.Should().Be("alice@firma.de");
    }

    [Fact]
    public async Task NullResultFromAdapter_TreatedAsInvalidCredentials_KeepsCircuitClosed()
    {
        var adapter = new FakeAdapter { Result = null };
        var breaker = new LdapCircuitBreaker(failureThreshold: 2);
        var auth = NewAuthenticator(EnabledOptions(), adapter, breaker);

        var outcome = await auth.AuthenticateAsync("alice", "wrong", default);

        outcome.Outcome.Should().Be(LdapAuthOutcome.InvalidCredentials);
        breaker.CurrentState.Should().Be(LdapCircuitBreaker.State.Closed);
    }

    [Fact]
    public async Task InfrastructureFailure_TripsBreaker_AfterThreshold()
    {
        var adapter = new FakeAdapter { ThrowInfra = true };
        var breaker = new LdapCircuitBreaker(failureThreshold: 2);
        var auth = NewAuthenticator(EnabledOptions(), adapter, breaker);

        var first = await auth.AuthenticateAsync("alice", "pw", default);
        first.Outcome.Should().Be(LdapAuthOutcome.Unavailable);
        first.UnavailableReason.Should().Be("infrastructure_failure");
        breaker.CurrentState.Should().Be(LdapCircuitBreaker.State.Closed);

        var second = await auth.AuthenticateAsync("bob", "pw", default);
        second.Outcome.Should().Be(LdapAuthOutcome.Unavailable);
        breaker.CurrentState.Should().Be(LdapCircuitBreaker.State.Open);

        // Subsequent calls fast-fail with circuit_open and never invoke the adapter again.
        var thirdCallsBefore = adapter.Calls;
        var third = await auth.AuthenticateAsync("eve", "pw", default);
        third.Outcome.Should().Be(LdapAuthOutcome.Unavailable);
        third.UnavailableReason.Should().Be("circuit_open");
        adapter.Calls.Should().Be(thirdCallsBefore);
    }

    [Fact]
    public async Task MalformedUsername_ReturnsInvalidCredentials_DoesNotTripBreaker()
    {
        var adapter = new FakeAdapter();
        var breaker = new LdapCircuitBreaker(failureThreshold: 2);
        var auth = NewAuthenticator(new LdapOptions { Enabled = true, UpnSuffix = null }, adapter, breaker);

        var outcome = await auth.AuthenticateAsync("alice", "pw", default);

        outcome.Outcome.Should().Be(LdapAuthOutcome.InvalidCredentials);
        breaker.CurrentState.Should().Be(LdapCircuitBreaker.State.Closed);
        adapter.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Cancellation_PropagatesAsTaskCanceled_NotAsUnavailable()
    {
        var adapter = new FakeAdapter { ThrowCancellation = true };
        var breaker = new LdapCircuitBreaker(failureThreshold: 2);
        var auth = NewAuthenticator(EnabledOptions(), adapter, breaker);

        Func<Task> act = () => auth.AuthenticateAsync("alice", "pw", new CancellationToken(true));

        await act.Should().ThrowAsync<OperationCanceledException>();
        // Cancellation is the caller's choice — must not punish LDAP availability.
        breaker.CurrentState.Should().Be(LdapCircuitBreaker.State.Closed);
    }

    private static LdapOptions EnabledOptions() => new()
    {
        Enabled = true,
        Server = "dc.local",
        BaseDn = "DC=firma,DC=de",
        UpnSuffix = "firma.de",
    };

    private static LdapAuthResult Sample(string upn) =>
        new("guid-x", upn, "Test User", Array.Empty<string>());

    private static LdapAuthenticator NewAuthenticator(
        LdapOptions options,
        ILdapConnectionAdapter adapter,
        LdapCircuitBreaker? breaker = null) =>
        new(
            new StaticOptionsMonitor<LdapOptions>(options),
            adapter,
            breaker ?? new LdapCircuitBreaker(failureThreshold: 2),
            NullLogger<LdapAuthenticator>.Instance);

    private sealed class FakeAdapter : ILdapConnectionAdapter
    {
        public LdapAuthResult? Result { get; set; }
        public bool ThrowInfra { get; set; }
        public bool ThrowCancellation { get; set; }
        public int Calls { get; private set; }
        public string? LastUpn { get; private set; }

        public Task<LdapAuthResult?> AuthenticateAsync(string upn, string password, CancellationToken ct)
        {
            Calls++;
            LastUpn = upn;
            if (ThrowCancellation) throw new OperationCanceledException(ct);
            if (ThrowInfra) throw new LdapInfrastructureException("simulated DC offline");
            return Task.FromResult(Result);
        }
    }

}
