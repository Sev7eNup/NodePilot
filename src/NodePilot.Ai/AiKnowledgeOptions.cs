namespace NodePilot.Ai;

/// <summary>
/// Configuration for the global "AI Chat" knowledge assistant (distinct from the workflow-scoped
/// designer chat). Deliberately flat — a master switch plus three per-source toggles and two root
/// paths; everything else is a code-side <c>const</c> token-budget knob no operator should touch.
///
/// <para>The three knowledge sources — documentation, live operational/workflow data, and repository
/// source code — are each independently enable-able by an Admin via the Settings UI (hot-reloadable).
/// Reads happen <b>live</b> at query time from the configured roots / the database, so future changes
/// to docs, code, or data flow in automatically with no re-index. The whole feature is gated by the
/// LLM master switch (<see cref="LlmOptions.Enabled"/>) as well as its own <see cref="Enabled"/>.</para>
/// </summary>
public class AiKnowledgeOptions
{
    public const string SectionName = "AiKnowledge";

    /// <summary>
    /// Max characters of a single file/snippet returned by a knowledge tool. A const because it's a
    /// token-budget tuning value, not an operator knob.
    /// </summary>
    public const int MaxSnippetChars = 1_600;

    /// <summary>Max total characters a single tool result may return to the LLM (truncated beyond).</summary>
    public const int MaxToolResultChars = 24_000;

    /// <summary>Master switch for the global knowledge chat. Default <c>false</c> — operator opt-in.
    /// When off (or when <see cref="LlmOptions.Enabled"/> is off), <c>POST /api/ai/knowledge/ask</c> responds 503.</summary>
    public bool Enabled { get; set; }

    /// <summary>Whether the documentation source (live keyword search over the docs corpus) is exposed to the chat.</summary>
    public bool DocsEnabled { get; set; } = true;

    /// <summary>Whether live operational/workflow data (installed workflows, executions, machines) is exposed to the chat.</summary>
    public bool OperationalEnabled { get; set; } = true;

    /// <summary>Whether the repository source code is exposed to the chat. Default <c>false</c> — an admin
    /// consciously enables it. Source-code tools are additionally restricted to Admin/Operator at request time.</summary>
    public bool SourceCodeEnabled { get; set; }

    /// <summary>Docs corpus root. Null/empty resolves to <c>{ContentRoot}/knowledge/docs</c> (shipped via the API csproj).</summary>
    public string? DocsRootPath { get; set; }

    /// <summary>Source tree root. Null/empty resolves to <c>{ContentRoot}/knowledge/source</c> (shipped by Build-Artifact.ps1).</summary>
    public string? SourceCodeRootPath { get; set; }

    /// <summary>Per-file read cap for the docs source (bytes). Files larger than this are skipped.</summary>
    public int DocsMaxFileBytes { get; set; } = 262_144;

    /// <summary>Max hits a docs search returns.</summary>
    public int DocsMaxResults { get; set; } = 20;

    /// <summary>Per-file read cap for the source-code source (bytes). Files larger than this are skipped.</summary>
    public int SourceCodeMaxFileBytes { get; set; } = 262_144;

    /// <summary>Max hits a source-code search returns.</summary>
    public int SourceCodeMaxResults { get; set; } = 20;
}
