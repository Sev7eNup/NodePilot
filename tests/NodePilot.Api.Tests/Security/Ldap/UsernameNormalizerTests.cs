using FluentAssertions;
using NodePilot.Api.Security.Ldap;
using Xunit;

namespace NodePilot.Api.Tests.Security.Ldap;

public class UsernameNormalizerTests
{
    [Theory]
    [InlineData("alice", "firma.de", "alice@firma.de")]
    [InlineData("  alice  ", "firma.de", "alice@firma.de")]
    [InlineData("ALICE", "firma.de", "alice@firma.de")]
    [InlineData("alice", "@firma.de", "alice@firma.de")]
    [InlineData("alice", "FIRMA.DE", "alice@firma.de")]
    public void ToUpn_BareLocalPart_AppendsSuffix(string raw, string suffix, string expected)
    {
        UsernameNormalizer.ToUpn(raw, suffix).Should().Be(expected);
    }

    [Theory]
    [InlineData("alice@firma.de", "firma.de", "alice@firma.de")]
    [InlineData("Alice@Firma.de", "firma.de", "alice@firma.de")]
    [InlineData("alice@other.tld", "firma.de", "alice@other.tld")]
    public void ToUpn_AlreadyUpn_PassesThrough(string raw, string suffix, string expected)
    {
        UsernameNormalizer.ToUpn(raw, suffix).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"FIRMA\alice", "firma.de", "alice@firma.de")]
    [InlineData(@"firma\Alice", "firma.de", "alice@firma.de")]
    [InlineData(@"DOMAIN\alice", "other.tld", "alice@other.tld")]
    public void ToUpn_DomainBackslashUser_StripsAndAppends(string raw, string suffix, string expected)
    {
        UsernameNormalizer.ToUpn(raw, suffix).Should().Be(expected);
    }

    [Fact]
    public void ToUpn_DomainBackslashUserWithExistingUpn_KeepsExistingDomain()
    {
        UsernameNormalizer.ToUpn(@"FIRMA\alice@override.tld", "firma.de")
            .Should().Be("alice@override.tld");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ToUpn_EmptyInput_Throws(string? raw)
    {
        Action act = () => UsernameNormalizer.ToUpn(raw!, "firma.de");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToUpn_OnlyDomainPrefix_Throws()
    {
        Action act = () => UsernameNormalizer.ToUpn(@"FIRMA\", "firma.de");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToUpn_BareUsername_NoSuffix_Throws()
    {
        Action act = () => UsernameNormalizer.ToUpn("alice", null);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToUpn_UpnInput_NoSuffix_StillWorks()
    {
        // A user that already typed the UPN doesn't need a suffix configured.
        UsernameNormalizer.ToUpn("alice@firma.de", null).Should().Be("alice@firma.de");
    }
}
