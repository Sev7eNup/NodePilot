namespace NodePilot.Core.Interfaces;

/// <summary>Reference to a failed step. StepId stays stable even for unlabeled steps.
/// Not named "FailedStepRef" because an API DTO already uses that name.</summary>
public sealed record FailedStepInfo(string StepId, string? StepName);

/// <summary>
/// Compact run overview for the AI chat. <see cref="ErrorMessage"/> is already redacted.
/// </summary>
public sealed record ExecutionLogSummary(
    Guid Id,
    string Status,
    DateTime StartedAt,
    DateTime? CompletedAt,
    string? TriggeredBy,
    string? ErrorMessage,
    int StepsTotal,
    IReadOnlyList<FailedStepInfo> FailedSteps);

/// <summary>Per-step view of a run. <see cref="Output"/> and <see cref="ErrorOutput"/> are already
/// redacted but not truncated; truncation is the consumer's responsibility.</summary>
public sealed record StepExecutionLog(
    string StepId,
    string? StepName,
    string StepType,
    string? TargetMachine,
    string Status,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    int AttemptCount,
    string? Output,
    string? ErrorOutput);

/// <summary>
/// A run together with its steps; the result of
/// <see cref="IExecutionLogReader.GetExecutionStepsAsync"/>.
/// </summary>
public sealed record ExecutionStepLogs(ExecutionLogSummary Execution, IReadOnlyList<StepExecutionLog> Steps);

/// <summary>
/// Read-only access to execution history for the AI chat assistant. All free-text fields pass
/// through secret redaction in the implementation (<see cref="Audit.IAuditDetailsRedactor"/>)
/// because the results go to an external LLM, regardless of the caller's privilege level.
/// <c>workflowId</c> is the server-side authorized scope (the controller checks folder RBAC);
/// <c>executionId</c> is validated separately.
/// </summary>
public interface IExecutionLogReader
{
    /// <summary>
    /// Most recent runs of the workflow, newest first. <paramref name="take"/> is clamped to
    /// [1,20].
    /// </summary>
    Task<IReadOnlyList<ExecutionLogSummary>> GetRecentExecutionsAsync(Guid workflowId, int take, CancellationToken ct);

    /// <summary>
    /// Null if the execution does not exist or does not belong to this workflow; the ownership
    /// check happens here.
    /// </summary>
    Task<ExecutionStepLogs?> GetExecutionStepsAsync(Guid workflowId, Guid executionId, CancellationToken ct);
}
