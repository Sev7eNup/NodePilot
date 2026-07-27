using System.Text.RegularExpressions;
using FluentAssertions;
using NodePilot.Api.Configuration;
using Xunit;

namespace NodePilot.Api.Tests.Architecture;

/// <summary>
/// The hot-reload split is an operational promise: a <c>true</c> section takes effect on save,
/// a <c>false</c> section shows the restart banner. CLAUDE.md states the two counts, and they
/// had drifted — the doc said 11 hot-reloadable while the code had 12, missing the
/// <c>Threading</c> section that <c>ThreadPoolTuningService</c> re-applies on every config
/// reload. Nothing noticed, because nothing counted.
///
/// <para>Same spirit as <c>DocumentationCountsTests</c> in the MCP suite: derive the number from
/// the code and fail when the prose disagrees.</para>
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
