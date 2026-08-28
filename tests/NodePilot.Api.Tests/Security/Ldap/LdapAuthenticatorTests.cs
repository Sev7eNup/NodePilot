using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Security.Ldap;
using NodePilot.Api.Tests.TestSupport;
using Xunit;
using NodePilot.TestCommons;

namespace NodePilot.Api.Tests.Security.Ldap;

public class LdapAuthenticatorTests
{
    [Fact]
    public async Task Disabled_ReturnsUnavailable_WithoutCallingAdapter()
    {
        var adapter = new FakeLdapConnectionAdapter();
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
        var adapter = new FakeLdapConnectionAdapter { Result = canned };
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
        // A zero-length password is an RFC 4513 unauthenticated bind that AD accepts with
        // LDAP_SUCCESS. It must be rejected as invalid credentials before the adapter's Bind
        // is ever reached — otherwise "knowing a username" bypasses the password.
        // Whitespace-only input is rejected the same way.
        var adapter = new FakeLdapConnectionAdapter { Result = Sample("alice@firma.de") };
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
        var adapter = new FakeLdapConnectionAdapter { Result = Sample("alice@firma.de") };
        var auth = NewAuthenticator(EnabledOptions(), adapter);

        await auth.AuthenticateAsync(@"FIRMA\Alice", "pw", default);

        adapter.LastUpn.Should().Be("alice@firma.de");
    }

    [Fact]
    public async Task NullResultFromAdapter_TreatedAsInvalidCredentials_KeepsCircuitClosed()
    {
        var adapter = new FakeLdapConnectionAdapter { Result = null };
        var breaker = new LdapCircuitBreaker(failureThreshold: 2);
        var auth = NewAuthenticator(EnabledOptions(), adapter, breaker);

        var outcome = await auth.AuthenticateAsync("alice", "wrong", default);

        outcome.Outcome.Should().Be(LdapAuthOutcome.InvalidCredentials);
        breaker.CurrentState.Should().Be(LdapCircuitBreaker.State.Closed);
    }

    [Fact]
    public async Task InfrastructureFailure_TripsBreaker_AfterThreshold()
    {
        var adapter = new FakeLdapConnectionAdapter { ThrowInfra = true };
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
    public async Task UserObjectMissing_ReturnsDirectoryObjectMissing_NeverTripsBreaker()
    {
        // An AD account whose userPrincipalName attribute is unset still binds (AD resolves
        // the implicit samAccountName@domain UPN), but the follow-up search then finds no
        // object. This is a per-account data problem, not an outage, so it must never trip
        // the breaker and block LDAP logins for every user. Repeat past the failure threshold
        // to prove it.
        var adapter = new FakeLdapConnectionAdapter { ThrowUserObjectMissing = true };
        var breaker = new LdapCircuitBreaker(failureThreshold: 2);
        var auth = NewAuthenticator(EnabledOptions(), adapter, breaker);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var outcome = await auth.AuthenticateAsync("alice", "pw", default);
            outcome.Outcome.Should().Be(LdapAuthOutcome.DirectoryObjectMissing);
            outcome.Result.Should().BeNull();
            outcome.UnavailableReason.Should().BeNull("this is not an availability problem");
        }

        breaker.CurrentState.Should().Be(LdapCircuitBreaker.State.Closed);
        adapter.Calls.Should().Be(5, "the breaker must never fast-fail this case");
    }

    [Fact]
    public async Task MalformedUsername_ReturnsInvalidCredentials_DoesNotTripBreaker()
    {
        var adapter = new FakeLdapConnectionAdapter();
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
        var adapter = new FakeLdapConnectionAdapter { ThrowCancellation = true };
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

}
