namespace NodePilot.Ai;

/// <summary>
/// Configuration for the LLM endpoint behind the AI features (script and workflow generation).
/// Deliberately kept flat — eight operator knobs in <c>appsettings.json</c>; everything else is a
/// const in code (variable cap, JSON retry count) because those are token-budget tuning values
/// that no operator should ever need to touch.
///
/// <para>
/// The transport is OpenAI-compatible (chat completions), so the same code works against OpenAI
/// Cloud, Ollama, LM Studio, vLLM, LocalAI, and llama.cpp servers. Local endpoints are the
/// preferred use case — the default <c>BaseUrl</c> happens to point at OpenAI, but this whole
/// feature is opt-in (<c>Enabled=false</c> by default).
/// </para>
/// </summary>
public class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>
    /// Cap on the number of upstream variables included in the script-generation prompt. Both the
    /// frontend (BFS ordering) and the backend trim to this value. A const because it's a
    /// token-budget tuning value, not an operator knob.
    /// </summary>
    public const int MaxUpstreamVariables = 30;

    /// <summary>
    /// Number of retries for workflow generation when the LLM response isn't parsable JSON. 1 is
    /// enough — more retries cost tokens and don't help with models that already support JSON
    /// mode. For local models without JSON mode, one retry with a "reply with ONLY JSON"
    /// follow-up is the best trade-off.
    /// </summary>
    public const int MaxJsonRetries = 1;

    /// <summary>Master switch. Default <c>false</c> — operator opt-in. When off, both AI endpoints respond with 503.</summary>
    public bool Enabled { get; set; }

    /// <summary>OpenAI-compatible chat-completions root. For Ollama, e.g. <c>http://localhost:11434/v1</c>.</summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>
    /// API key. OpenAI Cloud requires one; most local endpoints don't. Recommended way to set it:
    /// the <c>Llm__ApiKey</c> environment variable — a plaintext value in the settings file
    /// triggers a startup hardening warning (same as <c>Smtp:Password</c>).
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Model name. Both modes (script and workflow generation) use the same model.</summary>
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
    /// reliably, and many small local models don't. When <c>false</c>, the chat behaves exactly as
    /// before (no <c>tools</c> in the request).
    /// </summary>
    public bool EnableToolCalling { get; set; }

    /// <summary>Max. LLM rounds with tool calls per chat turn (guards against infinite loops). Default 4.</summary>
    public int ToolCallMaxDepth { get; set; } = 4;
}
