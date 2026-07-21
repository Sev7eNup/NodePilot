using System.Security.Principal;
using FluentAssertions;
using NodePilot.Api.Security.Ldap;
using Xunit;

namespace NodePilot.Api.Tests.Security.Ldap;

/// <summary>
/// The two spots where externally influenced values enter an LDAP search filter:
/// <see cref="SystemLdapConnectionAdapter.EscapeFilter"/> (login UPN, RFC 4515) and
/// <see cref="SystemLdapConnectionAdapter.SidToFilterValue"/> (sync subject, binary SID).
/// A missed escape here is a textbook LDAP-filter injection.
/// </summary>
public sealed class LdapFilterEscapingTests
{
    [Theory]
    [InlineData("*", @"\2a")]
    [InlineData("(", @"\28")]
    [InlineData(")", @"\29")]
    [InlineData("\\", @"\5c")]
    [InlineData("\0", @"\00")]
    public void EscapeFilter_Rfc4515Metacharacter_IsHexEscaped(string input, string expected)
    {
        SystemLdapConnectionAdapter.EscapeFilter(input).Should().Be(expected);
    }

    [Fact]
    public void EscapeFilter_WildcardInjectionPayload_IsFullyNeutralized()
    {
        // Classic filter-injection attempt as a login name: would widen
        // (userPrincipalName=<upn>) into a wildcard match and inject a second condition.
        var escaped = SystemLdapConnectionAdapter.EscapeFilter("*)(objectClass=*");

        escaped.Should().Be(@"\2a\29\28objectClass=\2a");
        escaped.Should().NotContainAny("*", "(", ")");
    }

    [Fact]
    public void EscapeFilter_BackslashPayload_CannotSmuggleEscapeSequences()
    {
        // A raw backslash must itself be escaped, otherwise an attacker could pre-build
        // "\2a"-style sequences that survive as wildcards after our own escaping.
        SystemLdapConnectionAdapter.EscapeFilter(@"admin\2a").Should().Be(@"admin\5c2a");
    }

    [Theory]
    [InlineData("alice@firma.de")]
    [InlineData("jürgen.müller@firma.de")]
    [InlineData("o'brien@firma.de")]
    public void EscapeFilter_BenignUpn_PassesThroughUnchanged(string upn)
    {
        SystemLdapConnectionAdapter.EscapeFilter(upn).Should().Be(upn);
    }

    [Fact]
    public void SidToFilterValue_ValidSid_ProducesOnlyHexEscapedBytes()
    {
        var value = SystemLdapConnectionAdapter.SidToFilterValue("S-1-5-21-1-2-3-1001");

        // Every byte must be emitted as an escaped \xx pair — no character of the
        // subject may reach the filter verbatim.
        value.Should().MatchRegex(@"^(\\[0-9a-f]{2})+$");
    }

    [Fact]
    public void SidToFilterValue_ValidSid_RoundTripsToSameIdentifier()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        var value = SystemLdapConnectionAdapter.SidToFilterValue(sid);

        var bytes = value.Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Select(hex => Convert.ToByte(hex, 16))
            .ToArray();
        new SecurityIdentifier(bytes, 0).ToString().Should().Be(sid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-sid")]
    [InlineData("*)(objectSid=*")]
    [InlineData("S-1-5-21-1-2-3-)(uid=admin")]
    public void SidToFilterValue_InvalidOrHostileSubject_Throws(string subject)
    {
        var act = () => SystemLdapConnectionAdapter.SidToFilterValue(subject);

        act.Should().Throw<LdapInfrastructureException>()
            .WithMessage("*not a valid AD SID*");
    }
}
