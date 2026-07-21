using System.ComponentModel.DataAnnotations;

namespace NodePilot.Api.Dtos.Settings;

/// <summary>
/// AiKnowledge section DTO for the Admin Settings API. Mirrors
/// <see cref="NodePilot.Ai.AiKnowledgeOptions"/> — the operator-tunable knobs for the global
/// "AI Chat" knowledge assistant. No secret fields: the three toggles govern which knowledge
/// sources (docs / operational data / source code) the chat may draw from, plus the two live-read
/// root paths and per-source read caps.
/// </summary>
public sealed class AiKnowledgeSettingsDto
{
    /// <summary>Master switch for the global knowledge chat (also gated by <c>Llm:Enabled</c>).</summary>
    public bool Enabled { get; set; }

    public bool DocsEnabled { get; set; } = true;
    public bool OperationalEnabled { get; set; } = true;

    /// <summary>Exposes the repository source code. Source-code tools are additionally Admin/Operator-only at request time.</summary>
    public bool SourceCodeEnabled { get; set; }

    /// <summary>Docs corpus root. Empty resolves to <c>{ContentRoot}/knowledge/docs</c>.</summary>
    [StringLength(1024)]
    public string? DocsRootPath { get; set; }

    /// <summary>Source tree root. Empty resolves to <c>{ContentRoot}/knowledge/source</c>.</summary>
    [StringLength(1024)]
    public string? SourceCodeRootPath { get; set; }

    [Range(4_096, 8_388_608)]
    public int DocsMaxFileBytes { get; set; } = 262_144;

    [Range(1, 100)]
    public int DocsMaxResults { get; set; } = 20;

    [Range(4_096, 8_388_608)]
    public int SourceCodeMaxFileBytes { get; set; } = 262_144;

    [Range(1, 100)]
    public int SourceCodeMaxResults { get; set; } = 20;
}
