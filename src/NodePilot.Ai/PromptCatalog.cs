using System.Reflection;

namespace NodePilot.Ai;

/// <summary>
/// Loads the static system prompts and the workflow example from embedded resources in this
/// assembly. Singleton: the resources are read once at startup and kept in memory. There is no
/// file-override path, so prompts are part of the build and versioned with the code that uses
/// them.
///
/// <para>The activity/definition reference (<c>activity-reference.md</c>) is a separate resource
/// so that the chat assistant (<see cref="AssistantSystemPrompt"/>) can reuse the same activity
/// knowledge without the generation output rules ("only {nodes,edges}", "no prose", "exactly one
/// trigger"), which would conflict with the chat response format.
/// <see cref="WorkflowSystemPrompt"/> recombines both pieces for generation.</para>
/// </summary>
public sealed class PromptCatalog
{
    private const string ScriptSystemResource = "NodePilot.Ai.Prompts.script-system.md";
    private const string WorkflowSystemRulesResource = "NodePilot.Ai.Prompts.workflow-system.md";
    private const string ActivityReferenceResource = "NodePilot.Ai.Prompts.activity-reference.md";
    private const string AssistantSystemResource = "NodePilot.Ai.Prompts.assistant-system.md";
    private const string KnowledgeSystemResource = "NodePilot.Ai.Prompts.knowledge-system.md";
    private const string WorkflowExampleResource = "NodePilot.Ai.Prompts.workflow-example.json";

    public string ScriptSystemPrompt { get; }

    /// <summary>Shared activity/definition reference (schema, catalog, variables, layout). Does not
    /// include output rules.</summary>
    public string ActivityReference { get; }

    /// <summary>Workflow-generation prompt: output rules + activity reference (combined).</summary>
    public string WorkflowSystemPrompt { get; }

    /// <summary>Chat-assistant prompt (explain + edit). Gets the activity reference injected
    /// separately.</summary>
    public string AssistantSystemPrompt { get; }

    /// <summary>Global "AI Chat" knowledge/operations assistant prompt (read-only,
    /// tool-driven).</summary>
    public string KnowledgeSystemPrompt { get; }

    public string WorkflowExampleJson { get; }

    public PromptCatalog()
    {
        var asm = typeof(PromptCatalog).Assembly;
        ScriptSystemPrompt = LoadResource(asm, ScriptSystemResource);
        ActivityReference = RenderActivityCatalog(LoadResource(asm, ActivityReferenceResource));
        AssistantSystemPrompt = LoadResource(asm, AssistantSystemResource);
        KnowledgeSystemPrompt = LoadResource(asm, KnowledgeSystemResource);
        WorkflowExampleJson = LoadResource(asm, WorkflowExampleResource);

        // Generation needs the output rules and the activity reference as one prompt. The drift
        // test scans both prompt files separately, so they are joined here at runtime.
        WorkflowSystemPrompt = LoadResource(asm, WorkflowSystemRulesResource)
                               + "\n\n"
                               + ActivityReference;
    }

    /// <summary>
    /// Substitutes the activity-catalog placeholder in the static reference with the catalog
    /// rendered from <c>ActivityCatalog</c> and <c>ActivityConfigReference</c>, so the prompt
    /// always lists every registered activity type.
    /// </summary>
    private static string RenderActivityCatalog(string reference)
    {
        if (!reference.Contains(ActivityCatalogPromptRenderer.PlaceholderToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Prompt resource '{ActivityReferenceResource}' does not contain the "
                + $"'{ActivityCatalogPromptRenderer.PlaceholderToken}' placeholder. Without it the AI prompts "
                + "would ship without any activity catalog.");
        }

        return reference.Replace(
            ActivityCatalogPromptRenderer.PlaceholderToken,
            ActivityCatalogPromptRenderer.Render(),
            StringComparison.Ordinal);
    }

    private static string LoadResource(Assembly asm, string name)
    {
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded prompt resource '{name}' not found. Ensure NodePilot.Ai.csproj includes " +
                $"<EmbeddedResource Include=\"Prompts/*.md;Prompts/*.json\" /> and the file exists.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
