using NodePilot.Ai;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace NodePilot.Ai.Tests;

/// <summary>
/// Smoke tests for the embedded-resource pipeline: all three prompts must be readable
/// from the compiled assembly, and the workflow example must be valid JSON. If these
/// tests fail, someone either broke the resource glob in the csproj, renamed a prompt
/// file, or committed invalid JSON — all three should be caught here immediately,
/// not on the first real LLM call in production.
/// </summary>
public class PromptCatalogTests
{
    [Fact]
    public void Constructor_LoadsScriptSystemPrompt()
    {
        var catalog = new PromptCatalog();
        catalog.ScriptSystemPrompt.Should().NotBeNullOrWhiteSpace();
        catalog.ScriptSystemPrompt.Should().Contain("PowerShell",
            "the script-system prompt should reference the target language");
    }

    [Fact]
    public void Constructor_LoadsWorkflowSystemPrompt()
    {
        var catalog = new PromptCatalog();
        catalog.WorkflowSystemPrompt.Should().NotBeNullOrWhiteSpace();
        catalog.WorkflowSystemPrompt.Should().Contain("nodes",
            "the workflow-system prompt should describe the JSON schema");
        // The composed prompt must still include the activity catalog, so the drift
        // test (PromptCatalogDriftTest) stays green and generation sees this knowledge.
        catalog.WorkflowSystemPrompt.Should().Contain("`runScript`");
        catalog.WorkflowSystemPrompt.Should().Contain("Output rules",
            "the generation prompt must keep its strict output rules");
    }

    [Fact]
    public void Constructor_LoadsActivityReference_WithoutGenerationOutputRules()
    {
        var catalog = new PromptCatalog();
        catalog.ActivityReference.Should().NotBeNullOrWhiteSpace();
        catalog.ActivityReference.Should().Contain("`runScript`");
        catalog.ActivityReference.Should().Contain("Activity catalog");
        // The generation-only output rules must NOT appear in this shared reference —
        // they would collide with the chat assistant's own envelope format.
        catalog.ActivityReference.Should().NotContain("Output rules");
    }

    [Fact]
    public void Constructor_LoadsAssistantSystemPrompt_WithStreamingDelimiterFormat()
    {
        var catalog = new PromptCatalog();
        catalog.AssistantSystemPrompt.Should().NotBeNullOrWhiteSpace();
        catalog.AssistantSystemPrompt.Should().Contain("Markdown");
        // Streaming format: prose first, then the delimiter — no longer the old JSON envelope.
        catalog.AssistantSystemPrompt.Should().Contain(WorkflowAssistantService.DefinitionDelimiter);
        catalog.AssistantSystemPrompt.Should().NotContain("\"modify\"",
            "the old JSON-envelope contract must be gone (it collides with streaming)");
    }

    [Fact]
    public void Constructor_LoadsWorkflowExampleAsValidJson()
    {
        var catalog = new PromptCatalog();
        catalog.WorkflowExampleJson.Should().NotBeNullOrWhiteSpace();

        // If someone breaks the example file, it's caught here — otherwise the LLM would
        // receive it as a few-shot example with broken brackets/quotes.
        var act = () => JsonDocument.Parse(catalog.WorkflowExampleJson);
        act.Should().NotThrow("the embedded workflow example must always be valid JSON");

        using var doc = JsonDocument.Parse(catalog.WorkflowExampleJson);
        doc.RootElement.TryGetProperty("nodes", out var nodes).Should().BeTrue();
        doc.RootElement.TryGetProperty("edges", out var edges).Should().BeTrue();
        nodes.GetArrayLength().Should().BeGreaterThan(0);
        edges.GetArrayLength().Should().BeGreaterThan(0);
    }
}
