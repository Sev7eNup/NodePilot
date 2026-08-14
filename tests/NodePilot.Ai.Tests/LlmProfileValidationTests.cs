using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace NodePilot.Ai.Tests;

/// <summary>
/// <see cref="LlmProfileValidation"/> is the single rule shared by the boot path
/// (<see cref="LlmServiceCollectionExtensions.AddNodePilotAi"/>) and the settings boot-validator.
/// If the two ever disagreed, a settings save could be accepted that then refuses to boot — these
/// tests pin the rule itself.
/// </summary>
public class LlmProfileValidationTests
{
    private static IConfiguration Config(params (string Key, string Value)[] entries)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => (string?)e.Value))
            .Build();

    [Fact]
    public void ValidateProfileEndpoints_Disabled_ChecksNothing()
    {
        var issues = LlmProfileValidation.ValidateProfileEndpoints(Config(
            ("Llm:Enabled", "false"),
            ("Llm:Profiles:a:BaseUrl", "http://169.254.169.254/v1")));

        issues.Should().BeEmpty();
    }

    [Fact]
    public void ValidateProfileEndpoints_EnabledWithCleanProfiles_ReturnsNoIssues()
    {
        var issues = LlmProfileValidation.ValidateProfileEndpoints(Config(
            ("Llm:Enabled", "true"),
            ("Llm:Profiles:a:BaseUrl", "https://api.openai.com/v1"),
            ("Llm:Profiles:b:BaseUrl", "http://localhost:11434/v1")));

        issues.Should().BeEmpty();
    }

    [Fact]
    public void ValidateProfileEndpoints_RemotePlaintextProfile_IsReportedBeforeSaveOrBoot()
    {
        var issues = LlmProfileValidation.ValidateProfileEndpoints(Config(
            ("Llm:Enabled", "true"),
            ("Llm:Profiles:remote:Name", "Remote HTTP"),
            ("Llm:Profiles:remote:BaseUrl", "http://llm.corp.example/v1")));

        issues.Should().ContainSingle();
        issues[0].ConfigKey.Should().Be("Llm:Profiles:remote:BaseUrl");
        issues[0].Message.Should().Contain("Remote HTTP").And.Contain("plaintext HTTP");
    }

    [Fact]
    public void ValidateProfileEndpoints_MetadataEndpointInInactiveProfile_IsReported()
    {
        var issues = LlmProfileValidation.ValidateProfileEndpoints(Config(
            ("Llm:Enabled", "true"),
            ("Llm:ActiveProfileId", "a"),
            ("Llm:Profiles:a:BaseUrl", "https://api.openai.com/v1"),
            ("Llm:Profiles:parked:Name", "Parked"),
            ("Llm:Profiles:parked:BaseUrl", "http://169.254.169.254/v1")));

        issues.Should().ContainSingle();
        issues[0].ConfigKey.Should().Be("Llm:Profiles:parked:BaseUrl");
        issues[0].Message.Should().Contain("Parked").And.Contain("cloud-metadata");
    }

    [Fact]
    public void ValidateProfileEndpoints_ProfileWithoutName_FallsBackToId()
    {
        var issues = LlmProfileValidation.ValidateProfileEndpoints(Config(
            ("Llm:Enabled", "true"),
            ("Llm:Profiles:nameless:BaseUrl", "http://metadata.google.internal/v1")));

        issues.Should().ContainSingle();
        issues[0].Message.Should().Contain("nameless");
    }

    [Fact]
    public void ValidateProfileEndpoints_ProfileWithoutBaseUrl_IsSkipped()
    {
        var issues = LlmProfileValidation.ValidateProfileEndpoints(Config(
            ("Llm:Enabled", "true"),
            ("Llm:Profiles:a:Model", "gpt-4o-mini")));

        issues.Should().BeEmpty();
    }

    [Fact]
    public void ValidateProxy_Disabled_ChecksNothing()
    {
        // Same gate as the profile check: an untouched block must never block a boot.
        LlmProfileValidation.ValidateProxy(Config(
            ("Llm:Enabled", "false"),
            ("Llm:Proxy:Mode", "Custom")))
            .Should().BeEmpty();
    }

    [Fact]
    public void ValidateProxy_ModeOffOrSystem_NeedsNoAddress()
    {
        LlmProfileValidation.ValidateProxy(Config(
            ("Llm:Enabled", "true"), ("Llm:Proxy:Mode", "Off"))).Should().BeEmpty();
        LlmProfileValidation.ValidateProxy(Config(
            ("Llm:Enabled", "true"), ("Llm:Proxy:Mode", "System"))).Should().BeEmpty();
    }

    [Fact]
    public void ValidateProxy_NoModeConfigured_ReturnsNoIssues()
    {
        LlmProfileValidation.ValidateProxy(Config(("Llm:Enabled", "true"))).Should().BeEmpty();
    }

    [Fact]
    public void ValidateProxy_UnknownMode_IsReported()
    {
        var issues = LlmProfileValidation.ValidateProxy(Config(
            ("Llm:Enabled", "true"),
            ("Llm:Proxy:Mode", "sometimes")));

        issues.Should().ContainSingle();
        issues[0].ConfigKey.Should().Be("Llm:Proxy:Mode");
    }

    [Fact]
    public void ValidateProxy_CustomWithoutAddress_IsReported()
    {
        var issues = LlmProfileValidation.ValidateProxy(Config(
            ("Llm:Enabled", "true"),
            ("Llm:Proxy:Mode", "Custom")));

        issues.Should().ContainSingle();
        issues[0].ConfigKey.Should().Be("Llm:Proxy:Address");
        issues[0].Message.Should().Contain("no proxy address is set");
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://proxy.corp.local:21")]
    [InlineData("proxy.corp.local:8080")]
    public void ValidateProxy_CustomWithNonHttpAddress_IsReported(string address)
    {
        var issues = LlmProfileValidation.ValidateProxy(Config(
            ("Llm:Enabled", "true"),
            ("Llm:Proxy:Mode", "Custom"),
            ("Llm:Proxy:Address", address)));

        issues.Should().ContainSingle();
        issues[0].ConfigKey.Should().Be("Llm:Proxy:Address");
    }

    [Fact]
    public void ValidateProxy_CustomWithMetadataAddress_IsReported()
    {
        // A proxy address is an outbound destination too — the metadata block applies to it.
        var issues = LlmProfileValidation.ValidateProxy(Config(
            ("Llm:Enabled", "true"),
            ("Llm:Proxy:Mode", "Custom"),
            ("Llm:Proxy:Address", "http://169.254.169.254:8080")));

        issues.Should().ContainSingle();
        issues[0].Message.Should().Contain("cloud-metadata");
    }

    [Fact]
    public void ValidateProxy_CustomWithValidAddress_ReturnsNoIssues()
    {
        LlmProfileValidation.ValidateProxy(Config(
            ("Llm:Enabled", "true"),
            ("Llm:Proxy:Mode", "custom"), // parsed case-insensitively, like the config binder
            ("Llm:Proxy:Address", "http://proxy.corp.local:8080")))
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("A", true)] // ids are matched case-insensitively
    [InlineData("gone", false)]
    [InlineData("", false)]
    public void HasResolvableActiveProfile_MatchesConfiguredProfiles(string activeId, bool expected)
    {
        LlmProfileValidation.HasResolvableActiveProfile(Config(
            ("Llm:ActiveProfileId", activeId),
            ("Llm:Profiles:a:BaseUrl", "https://api.openai.com/v1")))
            .Should().Be(expected);
    }

    [Fact]
    public void HasResolvableActiveProfile_NoProfilesAtAll_ReturnsFalse()
    {
        LlmProfileValidation.HasResolvableActiveProfile(Config(("Llm:ActiveProfileId", "a")))
            .Should().BeFalse();
    }
}
