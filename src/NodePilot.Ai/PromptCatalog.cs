using System.Reflection;

namespace NodePilot.Ai;

/// <summary>
/// Loads the static system prompts and the workflow example from embedded resources in the API
/// assembly. Singleton — the resources are read once at startup and then kept in memory. There is
/// no file-override path: prompts are part of the build and are versioned together with the code
/// that consumes them.
///
/// <para>The activity/definition reference (<c>activity-reference.md</c>) has been split out of
/// the workflow-generation prompt so that the chat assistant (<see cref="AssistantSystemPrompt"/>)
/// can reuse the same activity knowledge <b>without</b> the generation output rules ("only
/// {nodes,edges}", "no prose", "exactly one trigger") — those rules would conflict with the chat
/// response format. <see cref="WorkflowSystemPrompt"/> recombines both pieces for generation, so
/// the existing drift test and workflow generation stay unaffected.</para>
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

    /// <summary>Shared activity/definition reference (schema, catalog, variables, layout). Does not include output rules.</summary>
    public string ActivityReference { get; }

    /// <summary>Workflow-generation prompt: output rules + activity reference (combined).</summary>
    public string WorkflowSystemPrompt { get; }

    /// <summary>Chat-assistant prompt (explain + edit). Gets the activity reference injected separately.</summary>
    public string AssistantSystemPrompt { get; }

    /// <summary>Global "AI Chat" knowledge/operations assistant prompt (read-only, tool-driven).</summary>
    public string KnowledgeSystemPrompt { get; }

    public string WorkflowExampleJson { get; }

    public PromptCatalog()
    {
        var asm = typeof(PromptCatalog).Assembly;
        ScriptSystemPrompt = LoadResource(asm, ScriptSystemResource);
        ActivityReference = LoadResource(asm, ActivityReferenceResource);
        AssistantSystemPrompt = LoadResource(asm, AssistantSystemResource);
        KnowledgeSystemPrompt = LoadResource(asm, KnowledgeSystemResource);
        WorkflowExampleJson = LoadResource(asm, WorkflowExampleResource);

        // Generation needs the output rules + the activity reference combined into one prompt.
        // The drift test scans both prompt files separately; we join them here at runtime.
        WorkflowSystemPrompt = LoadResource(asm, WorkflowSystemRulesResource)
                               + "\n\n"
                               + ActivityReference;
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
