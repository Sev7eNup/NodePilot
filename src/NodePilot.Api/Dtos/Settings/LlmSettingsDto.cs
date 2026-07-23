using System.ComponentModel.DataAnnotations;

namespace NodePilot.Api.Dtos.Settings;

/// <summary>
/// LLM section DTO for the Admin Settings API. Mirrors <see cref="NodePilot.Ai.LlmOptions"/>
/// — the operator-tunable knobs only, the per-feature constants (<c>MaxUpstreamVariables</c>,
/// <c>MaxJsonRetries</c>) stay code-side and aren't exposed through the UI.
///
/// <para><c>ApiKey</c> follows the same Secret-handling rules as
/// <see cref="SmtpSettingsDto.Password"/>: <c>"********"</c> on read when set,
/// <c>"__unchanged__"</c> sentinel on write to keep the persisted value, new plaintext
/// to rotate, or <c>null</c>/empty to clear.</para>
/// </summary>
public sealed class LlmSettingsDto
{
    public bool Enabled { get; set; }

    [Required(AllowEmptyStrings = false)]
    [Url]
    [StringLength(2048)]
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>
    /// Read response: <c>"********"</c> when a value is configured, <c>null</c> otherwise.
    /// Write request: <c>"__unchanged__"</c> sentinel keeps it, new plaintext rotates,
    /// null/empty clears.
    /// </summary>
    public string? ApiKey { get; set; }

    [Required(AllowEmptyStrings = false)]
    [StringLength(255)]
    public string Model { get; set; } = "";

    /// <summary>
    /// Output-token cap per LLM call. 256–128k matches what real-world OpenAI-compatible
    /// endpoints accept; values outside this range are almost always operator typos
    /// (e.g. 40 instead of 4000) that would cause every LLM call to truncate or 400.
    /// </summary>
    [Range(256, 128_000)]
    public int MaxTokens { get; set; } = 4096;

    [Range(5, 3600)]
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// Opt-in: lets the workflow chat assistant call read-only analysis tools (function-calling).
    /// Default <c>false</c>; requires a model that reliably supports tool-calling.
    /// </summary>
    public bool EnableToolCalling { get; set; }

    /// <summary>Max LLM rounds with tool calls per chat turn (loop guard). 1–10.</summary>
    [Range(1, 10)]
    public int ToolCallMaxDepth { get; set; } = 6;
}
