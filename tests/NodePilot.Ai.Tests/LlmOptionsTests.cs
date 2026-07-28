using FluentAssertions;
using Xunit;

namespace NodePilot.Ai.Tests;

/// <summary>
/// Profile resolution is the one piece of behaviour on <see cref="LlmOptions"/>, and every AI
/// feature gates on it — a wrong answer here either talks to the wrong endpoint or 503s a working
/// installation.
/// </summary>
public class LlmOptionsTests
{
    private static LlmOptions WithProfiles(string activeId, params string[] ids) => new()
    {
        Enabled = true,
        ActiveProfileId = activeId,
        Profiles = ids.ToDictionary(
            id => id,
            id => new LlmProfileOptions { Name = id, BaseUrl = "https://api.openai.com/v1", Model = "m" },
            StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public void TryResolveActiveProfile_KnownId_ReturnsThatProfile()
    {
        WithProfiles("b", "a", "b").TryResolveActiveProfile(out var profile).Should().BeTrue();
        profile!.Name.Should().Be("b");
    }

    [Theory]
    [InlineData("B")]
    [InlineData(" b ")] // operator-entered ids are trimmed
    public void TryResolveActiveProfile_IdIsTrimmedAndCaseInsensitive(string activeId)
    {
        WithProfiles(activeId, "a", "b").TryResolveActiveProfile(out var profile).Should().BeTrue();
        profile!.Name.Should().Be("b");
    }

    [Fact]
    public void TryResolveActiveProfile_UnknownId_ReturnsFalse()
    {
        // No "fall back to the first profile" — that would silently switch endpoints.
        WithProfiles("gone", "a", "b").TryResolveActiveProfile(out var profile).Should().BeFalse();
        profile.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryResolveActiveProfile_BlankId_ReturnsFalse(string activeId)
    {
        WithProfiles(activeId, "a").TryResolveActiveProfile(out _).Should().BeFalse();
    }

    [Fact]
    public void TryResolveActiveProfile_NoProfiles_ReturnsFalse()
    {
        new LlmOptions { Enabled = true, ActiveProfileId = "a" }.TryResolveActiveProfile(out _).Should().BeFalse();
    }

    [Fact]
    public void IsUsable_RequiresBothTheKillSwitchAndAProfile()
    {
        WithProfiles("a", "a").IsUsable.Should().BeTrue();

        var disabled = WithProfiles("a", "a");
        disabled.Enabled = false;
        disabled.IsUsable.Should().BeFalse();

        new LlmOptions { Enabled = true }.IsUsable.Should().BeFalse();
    }

    [Fact]
    public void Defaults_AreOptInAndEmpty()
    {
        var options = new LlmOptions();
        options.Enabled.Should().BeFalse();
        options.ActiveProfileId.Should().BeEmpty();
        options.Profiles.Should().BeEmpty();
    }
}
