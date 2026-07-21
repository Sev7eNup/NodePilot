namespace NodePilot.Core.Interfaces;

/// <summary>
/// Instance-wide, read-only view of live operational/workflow data for the global "AI Chat"
/// knowledge assistant. Every method takes a pre-resolved <see cref="AccessibleFolderSet"/> so the
/// implementation never touches <c>ClaimsPrincipal</c> — the controller resolves folder access once
/// and passes it in. All free-text and workflow definitions are redacted before they leave the
/// reader (they are bound for an external LLM). Machines are global infrastructure (not folder
/// scoped) and never expose credentials.
/// </summary>
public interface IOperationalKnowledgeReader
{
    /// <summary>Installed workflows the caller may read, optionally filtered by a name substring.</summary>
    Task<IReadOnlyList<WorkflowKnowledgeSummary>> ListWorkflowsAsync(
        AccessibleFolderSet accessible, string? nameFilter, int take, CancellationToken ct);

    /// <summary>
    /// The secret-redacted definition of a single workflow, resolved by GUID or name (exact-case
    /// wins, else unique case-insensitive; ambiguous/unknown → null). Null when the caller can't
    /// read it. Redaction is key-based (<c>WorkflowSecretRedactor</c>) plus a pattern-based pass
    /// over the serialized definition (catches secrets hard-coded inside runScript bodies).
    /// </summary>
    Task<WorkflowKnowledgeDetail?> GetWorkflowDefinitionAsync(
        AccessibleFolderSet accessible, string idOrName, CancellationToken ct);

    /// <summary>Most recent executions across the whole instance (folder-scoped), optionally filtered by status.</summary>
    Task<IReadOnlyList<ExecutionKnowledgeSummary>> ListRecentExecutionsAsync(
        AccessibleFolderSet accessible, string? status, int take, CancellationToken ct);

    /// <summary>Recent executions of one specific workflow (resolved by GUID or name).</summary>
    Task<IReadOnlyList<ExecutionKnowledgeSummary>> GetWorkflowExecutionsAsync(
        AccessibleFolderSet accessible, string idOrName, int take, CancellationToken ct);

    /// <summary>Managed machines (global infra). Name/host/reachability/usage only — never credentials.</summary>
    Task<IReadOnlyList<MachineKnowledgeSummary>> ListMachinesAsync(int take, CancellationToken ct);

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

/// <summary>List-view of an installed workflow (no definition body).</summary>
public sealed record WorkflowKnowledgeSummary(
    Guid Id,
    string Name,
    string? Description,
    bool IsEnabled,
    int ActivityCount,
    IReadOnlyList<string> TriggerTypes,
    DateTime UpdatedAt);

/// <summary>A single workflow's secret-redacted definition, for content Q&amp;A + analysis.</summary>
public sealed record WorkflowKnowledgeDetail(
    Guid Id,
    string Name,
    string? Description,
    bool IsEnabled,
    string RedactedDefinitionJson);

/// <summary>One execution row (redacted error text).</summary>
public sealed record ExecutionKnowledgeSummary(
    Guid Id,
    Guid WorkflowId,
    string WorkflowName,
    string Status,
    DateTime StartedAt,
    DateTime? CompletedAt,
    string? TriggeredBy,
    string? ErrorMessage);

/// <summary>Upcoming scheduled fire times for one workflow's <c>scheduleTrigger</c> (all UTC instants).</summary>
public sealed record ScheduledFireForecast(
    Guid WorkflowId,
    string WorkflowName,
    string CronExpression,
    string? CronSummary,
    IReadOnlyList<DateTime> NextFiresUtc);

/// <summary>One managed machine (no credential material).</summary>
public sealed record MachineKnowledgeSummary(
    Guid Id,
    string Name,
    string Hostname,
    int WinRmPort,
    bool UseSsl,
    bool IsReachable,
    DateTime? LastConnectivityCheck,
    string? Tags);
