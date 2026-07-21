namespace NodePilot.Api.Dtos;

public record ExecutionCounts(int Total, int Succeeded, int Failed, int Running, int Cancelled);
public record HourBucket(DateTime HourStart, int Succeeded, int Failed, int Cancelled);
public record TopWorkflow(
    Guid Id, string Name, int RunCount, int SuccessCount, int FailCount,
    double? AvgDurationMs, double? P95DurationMs);
public record RunningExecutionInfo(Guid Id, Guid WorkflowId, string WorkflowName, string Status, DateTime StartedAt, string? TriggeredBy);
public record RecentExecutionInfo(
    Guid Id, Guid WorkflowId, string WorkflowName, string Status,
    DateTime StartedAt, DateTime? CompletedAt, long? DurationMs, string? TriggeredBy);

public record ArmedTriggerInfo(
    Guid WorkflowId, string WorkflowName, List<string> TriggerTypes,
    DateTime? NextFireUtc, string? NextFireKind, int? PollIntervalSeconds);

public record FailingWorkflow(
    Guid Id, string Name, int FailCount, int RunCount, DateTime? LastFailureAt,
    int PrevFailCount, int PrevRunCount);

public record EditLockInfo(
    Guid WorkflowId, string WorkflowName,
    string LockOwnerUserName, DateTime LockedAt);

public record HealthHeartbeatInfo(
    string ServiceName, DateTime LastHeartbeatAt,
    int ExpectedIntervalSeconds, string? Status, bool IsStale);

public record DashboardAuditEvent(
    DateTime Timestamp, string? ActorUserName,
    string Action, string? ResourceType, Guid? ResourceId);

public record DashboardStats(
    int WorkflowsTotal, int WorkflowsEnabled,
    int MachinesTotal, int MachinesReachable,
    int ExecutionsTotal,
    ExecutionCounts Last24h,
    List<HourBucket> Last24hBuckets,
    List<TopWorkflow> TopWorkflows,
    List<RunningExecutionInfo> Running,
    List<RecentExecutionInfo> Recent,
    List<ArmedTriggerInfo> ArmedTriggers,
    int PendingCount, int RunningCount, int LongRunningCount,
    List<FailingWorkflow> FailingWorkflows,
    List<EditLockInfo> EditLocks,
    List<HealthHeartbeatInfo> HealthHeartbeats,
    string DatabaseProvider,
    string? ClusterRole,
    List<DashboardAuditEvent>? RecentAudit,
    bool LlmEnabled);
