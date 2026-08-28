namespace NodePilot.Ai;

/// <summary>
/// Configuration for the global "AI Chat" knowledge assistant, distinct from the workflow-scoped
/// designer chat: a master switch, one toggle per knowledge source, and two root paths. Token
/// budgets stay as code-side <c>const</c> values.
///
/// <para>Documentation, live operational data, repository source code and read-only database access
/// are enabled separately by an Admin in the Settings UI and are hot-reloadable. Reads happen live
/// at query time from the configured roots or the database, so changes need no re-index. The
/// feature also requires the LLM master switch (<see cref="LlmOptions.Enabled"/>) on top of its own
/// <see cref="Enabled"/>.</para>
/// </summary>
public class AiKnowledgeOptions
{
    public const string SectionName = "AiKnowledge";

    /// <summary>Character cap for a single tool result; longer results are truncated.</summary>
    public const int MaxToolResultChars = 24_000;

    /// <summary>Master switch for the global knowledge chat, off by default.
    /// <c>POST /api/ai/knowledge/ask</c> responds 503 while this or
    /// <see cref="LlmOptions.Enabled"/> is off.</summary>
    public bool Enabled { get; set; }

    /// <summary>Whether the documentation source (live keyword search over the docs corpus) is
    /// exposed to the chat.</summary>
    public bool DocsEnabled { get; set; } = true;

    /// <summary>Whether live operational data (workflows, executions, machines) is exposed to the
    /// chat.</summary>
    public bool OperationalEnabled { get; set; } = true;

    /// <summary>Whether the repository source code is exposed to the chat, off by default.
    /// Source-code tools are additionally restricted to Admin/Operator at request time.</summary>
    public bool SourceCodeEnabled { get; set; }

    /// <summary>Whether read-only SQL access to the application database (text2sql) is exposed to
    /// the chat, off by default. Raw SQL can reach secret columns, so the source is restricted to
    /// global Admins and every result cell is secret-redacted (<c>***</c>) at the reader layer.
    /// Folder grants never elevate an Operator into this capability.</summary>
    public bool DbEnabled { get; set; }

    /// <summary>Docs corpus root. Null or empty resolves to <c>{ContentRoot}/knowledge/docs</c>,
    /// which is shipped via the API csproj.</summary>
    public string? DocsRootPath { get; set; }

    /// <summary>Source tree root. Null or empty resolves to <c>{ContentRoot}/knowledge/source</c>,
    /// which is shipped by Build-Artifact.ps1.</summary>
    public string? SourceCodeRootPath { get; set; }

    /// <summary>Per-file byte cap for the docs source. Larger files are skipped.</summary>
    public int DocsMaxFileBytes { get; set; } = 262_144;

    /// <summary>Max hits a docs search returns.</summary>
    public int DocsMaxResults { get; set; } = 20;

    /// <summary>Per-file byte cap for the source-code source. Larger files are skipped.</summary>
    public int SourceCodeMaxFileBytes { get; set; } = 262_144;

    /// <summary>Max hits a source-code search returns.</summary>
    public int SourceCodeMaxResults { get; set; } = 20;
}
