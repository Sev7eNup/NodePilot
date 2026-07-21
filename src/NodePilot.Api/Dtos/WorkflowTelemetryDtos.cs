namespace NodePilot.Api.Dtos;

public record StepHealthEntry(string Status, DateTime? StartedAt);

public record StepStats(
    int TotalRuns,
    int FailedRuns,
    double FailureRate,
    long AvgDurationMs,
    long P95DurationMs,
    long LastDurationMs);

/// <summary>
/// Coverage stats per node, computed from <c>StepExecutions</c> over the last
/// <c>windowDays</c>. Answers the "what logic was actually exercised in production?"
/// question. Three buckets keep "this branch never ran" distinct from "this branch
/// failed" — both are interesting but require different follow-up.
/// </summary>
public record NodeCoverageStats(
    string StepId,
    int ExecutedCount,
    int FailedCount,
    int SkippedCount,
    DateTime? LastExecutedAt,
    DateTime? LastSucceededAt,
    DateTime? LastFailedAt);

public record WorkflowCoverageResponse(
    Guid WorkflowId,
    int WindowDays,
    int TotalExecutions,
    DateTime? OldestExecutionInWindow,
    IReadOnlyList<NodeCoverageStats> Nodes);
