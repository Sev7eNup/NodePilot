namespace NodePilot.Ai;

/// <summary>
/// One named LLM connection: everything needed to talk to a single OpenAI-compatible endpoint.
/// Operators keep several side by side (for example OpenAI Cloud and a local Ollama), and
/// <see cref="LlmOptions.ActiveProfileId"/> selects the one that is live.
/// <see cref="EnableToolCalling"/> and <see cref="ToolCallMaxDepth"/> sit here rather than on
/// <see cref="LlmOptions"/> because reliable function calling is a property of the model, so
/// switching profiles has to carry the capability with it.
/// </summary>
public class LlmProfileOptions
{
    /// <summary>
    /// Operator-facing label (free text, renameable). The dictionary key is the id.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Address of the OpenAI-compatible endpoint. For Ollama, e.g.
    /// <c>http://localhost:11434/v1</c>.
    /// The path also picks the wire dialect (see <see cref="LlmEndpointGuard.ResolveEndpoint"/>): a
    /// URL ending in <c>/responses</c> speaks OpenAI's Responses API, one ending in
    /// <c>/chat/completions</c> is used verbatim, anything else gets <c>/chat/completions</c>
    /// appended.
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>
    /// API key. OpenAI Cloud requires one, most local endpoints do not. Preferred way to set it is
    /// the <c>Llm__Profiles__{id}__ApiKey</c> environment variable; a plaintext value in the
    /// settings file triggers a startup hardening warning (same as <c>Smtp:Password</c>).
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Model name used for every feature while this profile is active.</summary>
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// Cap on the LLM response length. The default is conservative so that response plus
    /// workflow-generation input still fits the small context window of a local model. Models with
    /// a larger context can take a higher value, but on smaller ones that provokes an upstream
    /// HTTP 400 for exceeded context length.
    /// </summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// HTTP timeout in seconds. Long enough for local models, short enough to not hang.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// Opt-in: lets the chat assistant call read-only MCP/analysis tools (OpenAI function calling,
    /// <c>tool_choice: auto</c>). Default <c>false</c>, because many small local models handle tool
    /// calling poorly. When <c>false</c>, the chat sends no <c>tools</c>.
    /// </summary>
    public bool EnableToolCalling { get; set; }

    /// <summary>Maximum LLM rounds with tool calls per chat turn, guarding against loops.</summary>
    public int ToolCallMaxDepth { get; set; } = 6;
}
