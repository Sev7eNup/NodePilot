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
