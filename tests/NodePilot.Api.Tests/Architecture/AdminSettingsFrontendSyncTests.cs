using System.Text.RegularExpressions;
using FluentAssertions;
using NodePilot.Api.Configuration;
using Xunit;

namespace NodePilot.Api.Tests.Architecture;

/// <summary>
/// Keeps the admin-settings surface in lock-step across the backend↔frontend seam — the one
/// risky mirror that lacked a drift guard while the activity/alerting catalogs and the Cli/Mcp
/// DTOs all had one (a gap identified by a July 2026 codebase-consistency audit, low-priority
/// finding "P3").
///
/// <para>The backend's authoritative section list is <see cref="SettingsSchema.Sections"/>
/// (each <c>SectionPath</c> is what the controller routes GET/PUT <c>/api/admin/settings/{section}</c>
/// on). The frontend requests those sections by string literal — either directly via
/// <c>getSection('X')</c>/<c>putSection('X')</c> or through the shared <c>useSectionForm('X', …)</c>
/// hook. If the two sets drift (backend adds a section with no UI, or the frontend references a
/// renamed/removed section) an operator silently loses the ability to view or edit that config —
/// so fail CI instead.</para>
/// </summary>
public class AdminSettingsFrontendSyncTests
{
    [Fact]
    public void FrontendAdminSettingsSections_MatchBackendSettingsSchema()
    {
        var backend = SettingsSchema.Sections.Select(s => s.SectionPath).ToHashSet(StringComparer.Ordinal);
        var frontend = ExtractFrontendSectionNames();

        frontend.Should().BeEquivalentTo(backend,
            "every SettingsSchema section must be surfaced by exactly one admin-settings component, and " +
            "every section the frontend requests via getSection/putSection/useSectionForm must be a real " +
            "backend section — update the frontend admin-settings components (or SettingsSchema.Sections) " +
            "when adding, removing, or renaming a settings section.");
    }

    /// <summary>
    /// Scans every component under <c>components/admin-settings/</c> and collects the section-name
    /// string literals passed to the three fetch/edit entry points. Deliberately narrow (only these
    /// call shapes) so a section name appearing in a comment or test-id can't inflate the set.
    /// </summary>
    private static HashSet<string> ExtractFrontendSectionNames()
    {
        var dir = Path.Combine(FindRepoRoot(), "src", "nodepilot-ui", "src", "components", "admin-settings");
        Directory.Exists(dir).Should().BeTrue($"admin-settings component folder must exist at {dir}");

        // getSection<...>('X') / putSection<...>('X') — dedicated-hook sections (Smtp/Llm/Retention/Authentication).
        // useSectionForm<...>('X', …)                — the shared generic-form sections (the other 15).
        // [^(]* skips the generic type args up to the opening paren without tripping on '{ … }' unions.
        var patterns = new[]
        {
            new Regex(@"(?:get|put)Section<[^(]*>\(\s*'([A-Za-z]+)'", RegexOptions.Compiled),
            new Regex(@"useSectionForm<[^(]*>\(\s*'([A-Za-z]+)'", RegexOptions.Compiled),
        };

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir, "*.tsx", SearchOption.AllDirectories))
        {
            var src = File.ReadAllText(file);
            foreach (var pattern in patterns)
                foreach (Match m in pattern.Matches(src))
                    names.Add(m.Groups[1].Value);
        }

        names.Should().NotBeEmpty("the scan must find the admin-settings section literals");
        return names;
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
