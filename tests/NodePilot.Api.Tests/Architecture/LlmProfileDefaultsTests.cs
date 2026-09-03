using System.Text.RegularExpressions;
using FluentAssertions;
using NodePilot.Ai;
using NodePilot.Api.Dtos.Settings;
using Xunit;

namespace NodePilot.Api.Tests.Architecture;

/// <summary>
/// Keeps the four copies of a new LLM profile's defaults in step.
///
/// <para>The same literals are written down in the options class that binds the configuration,
/// the settings DTO, the fallback the settings writer applies when a PUT omits the key, and the
/// form the admin UI seeds "Add profile" with. Nothing connected them, so raising a default in
/// one place quietly left operators with a different value depending on how the profile was
/// created.</para>
/// </summary>
public class LlmProfileDefaultsTests
{
    [Fact]
    public void MaxTokensDefault_AgreesAcrossOptionsDtoAndSettingsFallback()
    {
        var expected = new LlmProfileOptions().MaxTokens;

        expected.Should().Be(256_000);
        new LlmProfileSettingsDto().MaxTokens.Should().Be(expected);
        SettingsSectionsFallback("MaxTokens").Should().Be(expected);
    }

    [Fact]
    public void TimeoutSecondsDefault_AgreesAcrossOptionsAndDto()
    {
        var expected = new LlmProfileOptions().TimeoutSeconds;
        new LlmProfileSettingsDto().TimeoutSeconds.Should().Be(expected);
    }

    [Fact]
    public void MaxTokensDefault_IsWithinTheRangeTheApiAccepts()
    {
        // A default the settings endpoint would reject with 400 is worse than no default: the
        // profile binds fine at boot and only fails the first time someone saves the form.
        var range = RangeOf(nameof(LlmProfileSettingsDto.MaxTokens));
        new LlmProfileOptions().MaxTokens.Should().BeInRange(range.min, range.max);
    }

    [Fact]
    public void AdminUiAddProfileForm_SeedsTheSameMaxTokens()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(),
            "src", "nodepilot-ui", "src", "components", "admin-settings", "IntegrationsSection.tsx"));

        var match = Regex.Match(source, @"maxTokens:\s*(\d+)", RegexOptions.None, TimeSpan.FromSeconds(5));
        match.Success.Should().BeTrue("the Add-profile form seeds maxTokens");
        int.Parse(match.Groups[1].Value).Should().Be(new LlmProfileOptions().MaxTokens);
    }

    /// <summary>Reads the literal the settings writer falls back to when a PUT omits the key.</summary>
    private static int SettingsSectionsFallback(string key)
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(),
            "src", "NodePilot.Api", "Configuration", "SettingsSections.cs"));

        var match = Regex.Match(
            source,
            $@"{Regex.Escape(key)} = p\[""{Regex.Escape(key)}""\]\?\.GetValue<int>\(\) \?\? ([\d_]+)",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        match.Success.Should().BeTrue($"SettingsSections must declare a fallback for {key}");
        return int.Parse(match.Groups[1].Value.Replace("_", ""));
    }

    private static (int min, int max) RangeOf(string propertyName)
    {
        var attribute = typeof(LlmProfileSettingsDto)
            .GetProperty(propertyName)!
            .GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.RangeAttribute), false)
            .Cast<System.ComponentModel.DataAnnotations.RangeAttribute>()
            .Single();
        return ((int)attribute.Minimum, (int)attribute.Maximum);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "NodePilot.slnx")))
                return dir.FullName;
        throw new InvalidOperationException($"Could not locate NodePilot.slnx walking up from {AppContext.BaseDirectory}");
    }
}
