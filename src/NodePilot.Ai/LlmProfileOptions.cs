namespace NodePilot.Ai;

/// <summary>
/// One named LLM connection — everything needed to talk to a single OpenAI-compatible endpoint.
/// Operators keep several of these side by side (e.g. OpenAI Cloud and a local Ollama) and switch
/// between them by pointing <see cref="LlmOptions.ActiveProfileId"/> at one of them; exactly one
/// profile is live at a time.
///
/// <para>
/// <see cref="EnableToolCalling"/> and <see cref="ToolCallMaxDepth"/> live here rather than on
/// <see cref="LlmOptions"/> on purpose: reliable function-calling is a property of the model, not
/// of the installation. Switching to a small local model has to take the capability with it,
/// otherwise the chat would keep sending <c>tools</c> to an endpoint that mishandles them.
/// </para>
/// </summary>
public class LlmProfileOptions
{
    /// <summary>Operator-facing label (free text, renameable). The dictionary key is the stable id.</summary>
    public string Name { get; set; } = "";

    /// <summary>OpenAI-compatible chat-completions root. For Ollama, e.g. <c>http://localhost:11434/v1</c>.</summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>
    /// API key. OpenAI Cloud requires one; most local endpoints don't. Recommended way to set it:
    /// the <c>Llm__Profiles__{id}__ApiKey</c> environment variable — a plaintext value in the
    /// settings file triggers a startup hardening warning (same as <c>Smtp:Password</c>).
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Model name used for every feature while this profile is active.</summary>
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// Cap on the LLM response length. The default of 4096 is conservative and, combined with the
    /// workflow-generation input (~5-6k tokens), still fits inside the typical 8k context window
    /// of local models. Operators with more capable models (32k+ context) can raise it — but
    /// higher values can trigger an upstream HTTP 400 "context_length_exceeded" on smaller models.
    /// </summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>HTTP timeout in seconds. Generous enough for local models, but short enough to not hang forever.</summary>
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// Opt-in: lets the chat assistant call read-only MCP/analysis tools (OpenAI function calling,
    /// <c>tool_choice: auto</c>). Default <c>false</c> — the model needs to support tool calling
    /// reliably, and many small local models don't. When <c>false</c>, the chat sends no <c>tools</c>.
    /// </summary>
    public bool EnableToolCalling { get; set; }

    /// <summary>Max. LLM rounds with tool calls per chat turn (guards against infinite loops). Default 6.</summary>
    public int ToolCallMaxDepth { get; set; } = 6;
}
