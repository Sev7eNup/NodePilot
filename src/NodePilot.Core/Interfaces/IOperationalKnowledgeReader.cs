namespace NodePilot.Core.Interfaces;

/// <summary>
/// Read-only view of live workflow data for the global AI Chat knowledge assistant: a workflow's
/// secret-redacted definition and its computed scheduled-fire forecasts. Listing workflows,
/// executions and machines belongs to the <c>execute_readonly_sql</c> text2sql tools instead.
/// Every method takes a pre-resolved <see cref="AccessibleFolderSet"/>, so the implementation
/// never touches <c>ClaimsPrincipal</c>. Definitions are redacted before they reach the LLM.
/// </summary>
public interface IOperationalKnowledgeReader
{
    /// <summary>
    /// The secret-redacted definition of one workflow, resolved by GUID or name (exact case wins,
    /// otherwise a unique case-insensitive match). Null when unknown, ambiguous, or not readable
    /// by the caller. Redaction is key-based (<c>WorkflowSecretRedactor</c>) plus a pattern pass
    /// over the serialized definition, which also covers secrets hard-coded in runScript bodies.
    /// </summary>
    Task<WorkflowKnowledgeDetail?> GetWorkflowDefinitionAsync(
        AccessibleFolderSet accessible, string idOrName, CancellationToken ct);

    /// <summary>
    /// Upcoming fire times for enabled, folder-scoped workflows with an active
    /// <c>scheduleTrigger</c>, optionally narrowed to one workflow by GUID or name. Each entry
    /// lists the next <paramref name="perWorkflow"/> fires as UTC instants, computed from the
    /// trigger's <c>cronExpression</c> in the server's local time zone, matching the scheduler.
    /// </summary>
    Task<IReadOnlyList<ScheduledFireForecast>> ListScheduledFiresAsync(
        AccessibleFolderSet accessible, string? idOrName, int perWorkflow, int maxWorkflows, CancellationToken ct);
}

/// <summary>One workflow's secret-redacted definition, for questions about its content.</summary>
public sealed record WorkflowKnowledgeDetail(
    Guid Id,
    string Name,
    string? Description,
    bool IsEnabled,
    string RedactedDefinitionJson);

/// <summary>Upcoming fire times for one workflow's <c>scheduleTrigger</c>, in UTC.</summary>
public sealed record ScheduledFireForecast(
    Guid WorkflowId,
    string WorkflowName,
    string CronExpression,
    string? CronSummary,
    IReadOnlyList<DateTime> NextFiresUtc);
