using System.Text.RegularExpressions;
using FluentAssertions;
using NodePilot.Api.Configuration;
using Xunit;

namespace NodePilot.Api.Tests.Architecture;

/// <summary>
/// The hot-reload split is an operational promise: a <c>true</c> section takes effect on
/// save, a <c>false</c> section shows the restart banner. CLAUDE.md states the two counts,
/// so this derives them from <c>SettingsSchema</c> and fails if the doc disagrees — same
/// approach as <c>DocumentationCountsTests</c> in the MCP suite.
/// </summary>
public sealed class SettingsSchemaDocumentationTests
{
    [Fact]
    public void ClaudeMd_StatesTheActualHotReloadSplit()
    {
        var hot = SettingsSchema.Sections.Count(s => s.IsHotReloadable);
        var restart = SettingsSchema.Sections.Count(s => !s.IsHotReloadable);

        var claudeMd = File.ReadAllText(Path.Combine(ProductionSources.RepoRoot(), "CLAUDE.md"));
        var match = Regex.Match(claudeMd, @"(\d+) Sektionen sind hot-reloadable, (\d+) restart-pflichtig");

        match.Success.Should().BeTrue(
            "CLAUDE.md must still carry the hot-reload split claim — if the phrasing changed, " +
            "update this guard's pattern");
        int.Parse(match.Groups[1].Value).Should().Be(hot,
            "the documented hot-reloadable count must match SettingsSchema");
        int.Parse(match.Groups[2].Value).Should().Be(restart,
            "the documented restart-required count must match SettingsSchema");
    }

    /// <summary>
    /// Every descriptor must decide the question one way or the other, and the two buckets must
    /// account for all of them — otherwise the counts above could agree while a section is
    /// missing from the schema entirely.
    /// </summary>
    [Fact]
    public void EverySection_IsAccountedForInExactlyOneBucket()
    {
        var total = SettingsSchema.Sections.Length;
        total.Should().BeGreaterThan(15, "the settings surface covers the whole admin UI");

        (SettingsSchema.Sections.Count(s => s.IsHotReloadable)
         + SettingsSchema.Sections.Count(s => !s.IsHotReloadable))
            .Should().Be(total);

        SettingsSchema.Sections.Select(s => s.SectionPath).Should().OnlyHaveUniqueItems(
            "a duplicated SectionPath would make Find() return the wrong descriptor");
    }
}
