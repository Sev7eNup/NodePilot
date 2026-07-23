namespace NodePilot.Core.Interfaces;

/// <summary>
/// Instance-wide, read-only view of live workflow data for the global "AI Chat" knowledge
/// assistant — the slice that the database tools cannot provide: a workflow's secret-redacted
/// definition, deterministic structural analysis, and computed scheduled-fire forecasts. Listing
/// workflows, executions, and machines is intentionally NOT part of this reader; those are
/// answered by the <c>execute_readonly_sql</c> / text2sql tools against the app database. Every
/// method takes a pre-resolved <see cref="AccessibleFolderSet"/> so the implementation never
/// touches <c>ClaimsPrincipal</c> — the controller resolves folder access once and passes it in.
/// Workflow definitions are redacted before they leave the reader (they are bound for an external
/// LLM).
/// </summary>
public interface IOperationalKnowledgeReader
{
    /// <summary>
    /// The secret-redacted definition of a single workflow, resolved by GUID or name (exact-case
    /// wins, else unique case-insensitive; ambiguous/unknown → null). Null when the caller can't
    /// read it. Redaction is key-based (<c>WorkflowSecretRedactor</c>) plus a pattern-based pass
    /// over the serialized definition (catches secrets hard-coded inside runScript bodies).
    /// </summary>
    Task<WorkflowKnowledgeDetail?> GetWorkflowDefinitionAsync(
        AccessibleFolderSet accessible, string idOrName, CancellationToken ct);

    /// <summary>
    /// Upcoming fire times for enabled workflows that carry an active <c>scheduleTrigger</c>
    /// (folder-scoped), optionally narrowed to one workflow by GUID or name. Each entry lists the
    /// next <paramref name="perWorkflow"/> fires as UTC instants, computed from the trigger's
    /// <c>cronExpression</c> in the server's local time zone (mirroring the real scheduler) so the
    /// returned instants match when the workflow actually runs. Answers "when does the next/which
    /// scheduled workflow run" without the model having to guess from past executions.
    /// </summary>
    Task<IReadOnlyList<ScheduledFireForecast>> ListScheduledFiresAsync(
        AccessibleFolderSet accessible, string? idOrName, int perWorkflow, int maxWorkflows, CancellationToken ct);
}

/// <summary>A single workflow's secret-redacted definition, for content Q&amp;A + analysis.</summary>
public sealed record WorkflowKnowledgeDetail(
    Guid Id,
    string Name,
    string? Description,
    bool IsEnabled,
    string RedactedDefinitionJson);

/// <summary>Upcoming scheduled fire times for one workflow's <c>scheduleTrigger</c> (all UTC instants).</summary>
public sealed record ScheduledFireForecast(
    Guid WorkflowId,
    string WorkflowName,
    string CronExpression,
    string? CronSummary,
    IReadOnlyList<DateTime> NextFiresUtc);
