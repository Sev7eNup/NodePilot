using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using NodePilot.Api.Services.Backup;
using Xunit;

namespace NodePilot.Api.Tests.Architecture;

/// <summary>
/// <c>BackupController.ParsePolicies</c> expands a bare conflict policy ("overwrite") across
/// every section, enumerating from <see cref="BackupSections.All"/>. A hand-written array here
/// can drift behind <see cref="BackupSections"/>; <c>RestoreState.Policy</c> then answers
/// <c>Skip</c> for the missing sections, so a DR restore can report success while leaving
/// sections such as alerting rules, custom activities or global-variable folders untouched.
///
/// <para>These tests bind the enumeration to reality from both ends: every section constant is
/// listed in <see cref="BackupSections.All"/>, and every section the restore service actually
/// asks a policy for is covered by it.</para>
/// </summary>
public sealed class BackupSectionCoverageTests
{
    /// <summary>Section-name constants, excluding the schema/version strings next to
    /// them.</summary>
    private static readonly string[] SectionConstants = typeof(BackupSections)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Where(f => !f.Name.StartsWith("Schema", StringComparison.Ordinal)
                    && f.Name != "CurrentSchema")
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();

    [Fact]
    public void All_ContainsEverySectionConstant()
    {
        BackupSections.All.Should().BeEquivalentTo(SectionConstants,
            "a new section constant must join BackupSections.All, otherwise the global " +
            "conflict policy silently skips it during restore");
    }

    [Fact]
    public void All_HasNoDuplicates()
    {
        BackupSections.All.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EverySectionTheRestoreServiceAsksAbout_IsCoveredByAll()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "NodePilot.Api", "Services", "Backup", "BackupRestoreService.cs"));

        var queried = Regex.Matches(source, @"\.Policy\(BackupSections\.(\w+)\)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        queried.Should().NotBeEmpty("the restore service resolves a conflict policy per section");

        var covered = queried
            .Select(name => (string)typeof(BackupSections).GetField(name)!.GetRawConstantValue()!)
            .Where(value => !BackupSections.All.Contains(value))
            .ToList();

        covered.Should().BeEmpty(
            "jede Sektion, für die RestoreAsync eine Policy abfragt, muss in BackupSections.All " +
            "stehen — sonst greift die globale Konflikt-Policy dort nicht und der Restore " +
            "überspringt sie stillschweigend");
    }

    /// <summary>
    /// The controller must not reintroduce a local copy of the section list — a hand-written
    /// copy can silently drift behind BackupSections.All.
    /// </summary>
    [Fact]
    public void ParsePolicies_EnumeratesFromBackupSectionsAll()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "NodePilot.Api", "Controllers", "BackupController.cs"));

        var parse = source.IndexOf("ParsePolicies", StringComparison.Ordinal);
        parse.Should().BeGreaterThanOrEqualTo(0);

        source[parse..].Should().Contain("BackupSections.All",
            "ParsePolicies must expand a bare policy from the shared list, not a local array");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && directory is not null; depth++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NodePilot.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Could not locate NodePilot.slnx from the test output directory.");
    }
}
