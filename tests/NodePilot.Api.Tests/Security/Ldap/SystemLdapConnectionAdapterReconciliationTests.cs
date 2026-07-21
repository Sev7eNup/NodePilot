using FluentAssertions;
using NodePilot.Api.Security.Ldap;
using Xunit;

namespace NodePilot.Api.Tests.Security.Ldap;

public sealed class SystemLdapConnectionAdapterReconciliationTests
{
    private const string Subject = "S-1-5-21-1-2-3-1001";

    [Fact]
    public void FoundAndNotFound_IsAmbiguous()
    {
        var observations = new[]
        {
            Found(enabled: true, "S-1-5-21-1-2-3-2001"),
            NotFound(),
        };

        var reconcile = () =>
            SystemLdapConnectionAdapter.ReconcileEndpointResults(Subject, observations);

        reconcile.Should().Throw<LdapInfrastructureException>()
            .WithMessage("*disagree on object existence*");
    }

    [Fact]
    public void EnabledAndDisabled_IsAmbiguous()
    {
        var observations = new[]
        {
            Found(enabled: true, "S-1-5-21-1-2-3-2001"),
            Found(enabled: false, "S-1-5-21-1-2-3-2001"),
        };

        var reconcile = () =>
            SystemLdapConnectionAdapter.ReconcileEndpointResults(Subject, observations);

        reconcile.Should().Throw<LdapInfrastructureException>()
            .WithMessage("*disagree on account enabled state*");
    }

    [Fact]
    public void GroupRemovedOnOneDc_IsAmbiguous()
    {
        var observations = new[]
        {
            Found(enabled: true,
                "S-1-5-21-1-2-3-2001",
                "S-1-5-21-1-2-3-2002"),
            Found(enabled: true, "S-1-5-21-1-2-3-2001"),
        };

        var reconcile = () =>
            SystemLdapConnectionAdapter.ReconcileEndpointResults(Subject, observations);

        reconcile.Should().Throw<LdapInfrastructureException>()
            .WithMessage("*disagree on group membership*");
    }

    [Fact]
    public void OneUnavailableDc_CannotRefreshFreshness()
    {
        var observations = new[]
        {
            Found(enabled: true, "S-1-5-21-1-2-3-2001"),
            new LdapDirectoryLookupResult(
                Subject, null, new LdapInfrastructureException("dc unavailable")),
        };

        var reconcile = () =>
            SystemLdapConnectionAdapter.ReconcileEndpointResults(Subject, observations);

        reconcile.Should().Throw<LdapInfrastructureException>()
            .WithMessage("*could not be confirmed by every configured LDAP endpoint*");
    }

    [Fact]
    public void AllDcsNotFound_ConfirmsDestructiveAbsence()
    {
        var result = SystemLdapConnectionAdapter.ReconcileEndpointResults(
            Subject, [NotFound(), NotFound()]);

        result.Should().BeNull();
    }

    [Fact]
    public void AllDcsAgree_ReturnsFreshSnapshot()
    {
        var first = Found(enabled: true,
            "S-1-5-21-1-2-3-2001",
            "S-1-5-21-1-2-3-2002");
        var second = Found(enabled: true,
            "S-1-5-21-1-2-3-2002",
            "S-1-5-21-1-2-3-2001");

        var result = SystemLdapConnectionAdapter.ReconcileEndpointResults(
            Subject, [first, second]);

        result.Should().BeSameAs(first.Snapshot);
    }

    [Fact]
    public void PasswordBind_UsesConsensusGroups_NotStaleBindDcGroups()
    {
        const string removedAllowedGroup = "S-1-5-21-1-2-3-2001";
        const string currentGroup = "S-1-5-21-1-2-3-2002";
        var consensus = new LdapDirectorySnapshot(
            Subject, true, "alice@example.test", "Alice", [currentGroup]);

        var result = SystemLdapConnectionAdapter.BuildAuthoritativeAuthenticationResult(
            Subject, "55e2d28b-3dee-42c3-902a-24b79334b999", consensus);

        result.Should().NotBeNull();
        result!.GroupSids.Should().Equal(currentGroup);
        result.GroupSids.Should().NotContain(removedAllowedGroup);
    }

    [Fact]
    public void PasswordBind_ConsensusDisabled_DeniesLogin()
    {
        var consensus = new LdapDirectorySnapshot(
            Subject, false, "alice@example.test", "Alice", ["S-1-5-21-1-2-3-2001"]);

        var result = SystemLdapConnectionAdapter.BuildAuthoritativeAuthenticationResult(
            Subject, "55e2d28b-3dee-42c3-902a-24b79334b999", consensus);

        result.Should().BeNull();
    }

    [Fact]
    public void PasswordBind_AllDcsNotFound_DeniesLogin()
    {
        var result = SystemLdapConnectionAdapter.BuildAuthoritativeAuthenticationResult(
            Subject, "55e2d28b-3dee-42c3-902a-24b79334b999", null);

        result.Should().BeNull();
    }

    private static LdapDirectoryLookupResult Found(bool enabled, params string[] groups) =>
        new(
            Subject,
            new LdapDirectorySnapshot(
                Subject, enabled, "alice@example.test", "Alice", groups),
            null);

    private static LdapDirectoryLookupResult NotFound() =>
        new(Subject, null, null);
}
