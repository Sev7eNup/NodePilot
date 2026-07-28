using FluentAssertions;
using NodePilot.Core.Activities;
using Xunit;

namespace NodePilot.Ai.Tests;

/// <summary>
/// Keeps the AI prompts aligned with the backend-owned activity catalog.
///
/// <para>The activity list used to be hand-maintained Markdown, and the old version of this test
/// only asserted that a type's NAME appeared somewhere in the file. That let two failure modes
/// through: an activity could be deliberately withheld (<c>llmQuery</c> was, so the model reached
/// for <c>restApi</c> when a workflow needed an AI call), and a listed activity could carry wrong or
/// missing config keys and still pass. The catalog section is now rendered from
/// <see cref="ActivityCatalog"/> + <see cref="ActivityConfigReference"/>, and these tests assert
/// completeness down to the required config keys.</para>
/// </summary>
public class PromptCatalogDriftTest
{
    private static readonly string Reference = new PromptCatalog().ActivityReference;

    [Fact]
    public void EveryCatalogActivityType_AppearsInTheRenderedReference()
    {
        var missing = ActivityCatalog.All
            .Select(a => a.Type)
            .Where(t => !Reference.Contains($"`{t}`", StringComparison.Ordinal))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        missing.Should().BeEmpty(
            "every activity type must be visible to the AI prompts — a hidden type makes the model "
            + "pick a wrong-but-visible activity instead: {0}",
            string.Join(", ", missing));
    }

    [Fact]
    public void EveryCatalogActivityType_HasACuratedConfigReference()
    {
        var undocumented = ActivityCatalog.All
            .Select(a => a.Type)
            .Where(t => ActivityConfigReference.TryGet(t) is null)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        undocumented.Should().BeEmpty(
            "the rendered prompt falls back to a placeholder line for these types: {0}",
            string.Join(", ", undocumented));
    }

    [Fact]
    public void EveryRequiredConfigKey_AppearsInTheRenderedReference()
    {
        var missing = (from activity in ActivityCatalog.All
                       let entry = ActivityConfigReference.TryGet(activity.Type)
                       where entry is not null
                       from key in entry.ConfigKeys
                       where key.Required
                       where !Reference.Contains($"`{key.Key}`", StringComparison.Ordinal)
                       select $"{activity.Type}.{key.Key}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        missing.Should().BeEmpty(
            "a required config key the model never sees produces structurally invalid nodes: {0}",
            string.Join(", ", missing));
    }

    [Fact]
    public void RenderedReference_SteersLlmCallsToLlmQueryRatherThanRestApi()
    {
        // The regression this whole mechanism exists for: `llmQuery` was withheld from the prompt,
        // so generation emitted a hand-rolled OpenAI POST on a `restApi` node instead.
        Reference.Should().Contain("`llmQuery`");
        Reference.Should().Contain("`restApi`");

        var restApiEntry = ActivityConfigReference.TryGet("restApi");
        restApiEntry.Should().NotBeNull();
        restApiEntry!.PromptNotes.Should().Contain(
            n => n.Contains("llmQuery", StringComparison.Ordinal),
            "restApi must point at llmQuery so the model stops hand-building LLM HTTP calls");

        var generateTextEntry = ActivityConfigReference.TryGet("generateText");
        generateTextEntry!.PromptNotes.Should().Contain(
            n => n.Contains("NOT an AI", StringComparison.OrdinalIgnoreCase)
                 || n.Contains("not an AI", StringComparison.OrdinalIgnoreCase),
            "generateText reads like an LLM activity but generates random characters");
    }

    [Fact]
    public void RenderedReference_KeepsTheStaticSectionsAndDropsThePlaceholder()
    {
        Reference.Should().NotContain(ActivityCatalogPromptRenderer.PlaceholderToken);
        Reference.Should().Contain("Activity catalog");
        Reference.Should().Contain("## Variable substitution");
        Reference.Should().Contain("## Layout style");
    }
}
