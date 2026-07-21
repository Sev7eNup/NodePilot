using FluentAssertions;
using NodePilot.Core.Activities;
using Xunit;

namespace NodePilot.Ai.Tests;

/// <summary>
/// Keeps the AI workflow prompt aligned with the backend-owned activity catalog.
/// </summary>
public class PromptCatalogDriftTest
{
    [Fact]
    public void PromptIncludedActivityTypes_AppearInWorkflowSystemPrompt()
    {
        var repoRoot = FindRepoRoot();
        var promptText = ReadPromptFile(repoRoot);

        var missing = ActivityCatalog.All
            .Where(a => a.Prompt.IsIncluded)
            .Select(a => a.Type)
            .Where(t => !PromptMentionsActivityType(promptText, t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        missing.Should().BeEmpty(
            "activity types marked Prompt.IsIncluded in ActivityCatalog must be listed in workflow-system.md: {0}",
            string.Join(", ", missing));
    }

    [Fact]
    public void PromptExcludedActivityTypes_DoNotAppearInWorkflowSystemPrompt()
    {
        var repoRoot = FindRepoRoot();
        var promptText = ReadPromptFile(repoRoot);

        var stillInPrompt = ActivityCatalog.All
            .Where(a => !a.Prompt.IsIncluded)
            .Select(a => a.Type)
            .Where(t => PromptMentionsActivityType(promptText, t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        stillInPrompt.Should().BeEmpty(
            "activity types marked Prompt.IsIncluded=false are present in the prompt; update ActivityCatalog prompt metadata: {0}",
            string.Join(", ", stillInPrompt));
    }

    [Fact]
    public void PromptExclusions_HaveReasons()
    {
        ActivityCatalog.All
            .Where(a => !a.Prompt.IsIncluded)
            .Should()
            .OnlyContain(a => !string.IsNullOrWhiteSpace(a.Prompt.ExclusionReason));
    }

    private static string ReadPromptFile(string repoRoot)
    {
        // The activity catalog lives in the shared activity-reference.md (composed into
        // WorkflowSystemPrompt for generation, injected standalone into the chat assistant).
        // We scan both prompt files so the drift guard holds wherever an activity type is named.
        var promptsDir = Path.Combine(repoRoot, "src", "NodePilot.Ai", "Prompts");
        var systemPath = Path.Combine(promptsDir, "workflow-system.md");
        var referencePath = Path.Combine(promptsDir, "activity-reference.md");
        File.Exists(systemPath).Should().BeTrue($"workflow-system.md must exist at {systemPath}");
        File.Exists(referencePath).Should().BeTrue($"activity-reference.md must exist at {referencePath}");
        return File.ReadAllText(systemPath) + "\n" + File.ReadAllText(referencePath);
    }

    private static bool PromptMentionsActivityType(string promptText, string activityType) =>
        promptText.Contains($"`{activityType}`", StringComparison.Ordinal);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NodePilot.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException(
            $"Could not locate NodePilot.slnx walking up from {AppContext.BaseDirectory}");
    }
}
