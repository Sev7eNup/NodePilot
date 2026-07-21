using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace NodePilot.Data.Tests;

/// <summary>
/// Single source of truth for the DPAPI scope used by every encrypted-at-rest store.
/// Security-audit finding L-5: previous behaviour silently folded typos into CurrentUser, which
/// caused service-account-rotation to fail with no diagnostic — these tests pin the
/// strict-match-or-throw contract.
/// </summary>
public class DpapiScopeResolverTests
{
    private static IConfiguration ConfigWith(string? scope)
    {
        var dict = new Dictionary<string, string?>();
        if (scope is not null) dict["Credentials:DpapiScope"] = scope;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Fact]
    public void FromConfig_NullConfig_DefaultsToCurrentUser()
    {
        DpapiScopeResolver.FromConfig(null).Should().Be(DataProtectionScope.CurrentUser);
    }

    [Fact]
    public void FromConfig_MissingKey_DefaultsToCurrentUser()
    {
        DpapiScopeResolver.FromConfig(ConfigWith(null))
            .Should().Be(DataProtectionScope.CurrentUser);
    }

    [Fact]
    public void FromConfig_EmptyValue_DefaultsToCurrentUser()
    {
        DpapiScopeResolver.FromConfig(ConfigWith(""))
            .Should().Be(DataProtectionScope.CurrentUser);
    }

    [Fact]
    public void FromConfig_WhitespaceValue_DefaultsToCurrentUser()
    {
        DpapiScopeResolver.FromConfig(ConfigWith("   "))
            .Should().Be(DataProtectionScope.CurrentUser);
    }

    [Theory]
    [InlineData("CurrentUser")]
    [InlineData("currentuser")]
    [InlineData("CURRENTUSER")]
    [InlineData(" CurrentUser ")]
    public void FromConfig_CurrentUserVariants_ReturnsCurrentUser(string val)
    {
        DpapiScopeResolver.FromConfig(ConfigWith(val))
            .Should().Be(DataProtectionScope.CurrentUser);
    }

    [Theory]
    [InlineData("LocalMachine")]
    [InlineData("localmachine")]
    [InlineData("LOCALMACHINE")]
    [InlineData(" LocalMachine ")]
    public void FromConfig_LocalMachineVariants_ReturnsLocalMachine(string val)
    {
        DpapiScopeResolver.FromConfig(ConfigWith(val))
            .Should().Be(DataProtectionScope.LocalMachine);
    }

    [Theory]
    [InlineData("Local_Machine")]
    [InlineData("Machine")]
    [InlineData("user")]
    [InlineData("System")]
    public void FromConfig_UnrecognisedValue_ThrowsWithDiagnostic(string val)
    {
        Action act = () => DpapiScopeResolver.FromConfig(ConfigWith(val));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*'{val.Trim()}'*not recognized*");
    }
}
