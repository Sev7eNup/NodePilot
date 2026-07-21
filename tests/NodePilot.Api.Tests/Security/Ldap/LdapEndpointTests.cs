using FluentAssertions;
using NodePilot.Api.Security.Ldap;
using Xunit;

namespace NodePilot.Api.Tests.Security.Ldap;

public sealed class LdapEndpointTests
{
    [Fact]
    public void Resolve_PreservesConfiguredFailoverOrder()
    {
        var endpoints = LdapEndpoint.Resolve(
            ["dc01.example.test:636", "ldaps://dc02.example.test:1636"],
            legacyServer: null,
            legacyPort: 636);

        endpoints.Should().Equal(
            new LdapEndpoint("dc01.example.test", 636),
            new LdapEndpoint("dc02.example.test", 1636));
    }

    [Fact]
    public void Resolve_UsesLegacyServerWhenEndpointListIsEmpty()
    {
        LdapEndpoint.Resolve([], "legacy-dc.example.test", 636)
            .Should().ContainSingle()
            .Which.Should().Be(new LdapEndpoint("legacy-dc.example.test", 636));
    }

    [Fact]
    public void Resolve_HostWithoutPort_UsesLdapsDefault636()
    {
        LdapEndpoint.Resolve(["dc01.example.test"], null, 636)
            .Should().ContainSingle()
            .Which.Should().Be(new LdapEndpoint("dc01.example.test", 636));
    }

    [Theory]
    [InlineData("ldap://dc01.example.test:389")]
    [InlineData("ldaps://dc01.example.test:636/path")]
    [InlineData("not a host")]
    public void Resolve_RejectsUnsafeOrMalformedEndpoint(string value)
    {
        var act = () => LdapEndpoint.Resolve([value], null, 636);
        act.Should().Throw<LdapInfrastructureException>();
    }
}
