namespace NodePilot.Cli.Api.Dtos;

// CLI-side mirror of NodePilot.Api.Dtos.OperationsGraphDto (no ProjectReference per convention).

public sealed record OperationsGraphResponse(
    IReadOnlyList<OpsNodeDto> Nodes,
    IReadOnlyList<OpsEdgeDto> Edges,
    IReadOnlyList<OpsRunningExecutionDto> Running,
    IReadOnlyList<OpsRecentExecutionDto> Recent,
    OpsSnapshotMetaDto Meta);

/// <param name="OverdueSeconds">Long-running threshold (Alerting:LongRunningSeconds).</param>
/// <param name="WindowMinutes">Clamped look-back window this snapshot was built for.</param>
/// <param name="RecentSinceUtc">Left edge the caller asked for.</param>
/// <param name="OldestReturnedCompletedAt">Oldest settled run actually returned; null when none.</param>
/// <param name="RecentTruncated">More settled runs existed in the window than the cap returns.</param>
public sealed record OpsSnapshotMetaDto(
    int OverdueSeconds,
    int WindowMinutes,
    DateTime RecentSinceUtc,
    DateTime? OldestReturnedCompletedAt,
    bool RecentTruncated);

/// <param name="CanRun">Caller may cancel / retry / cancel-all on this workflow (folder Run right).</param>
/// <param name="CanEdit">Caller may disable / quarantine this workflow (folder Edit right).</param>
public sealed record OpsNodeDto(
    Guid WorkflowId,
    string Name,
    Guid FolderId,
    string FolderPath,
    bool IsEnabled,
    int RunningCount,
    string? LastStatus,
    int? CallFrequency,
    bool CanRun,
    bool CanEdit);

public sealed record OpsEdgeDto(
    string Id,
    Guid Source,
    Guid? Target,
    string Kind,
    string RefStatus,
    string RawRef,
    int CallCount);

public sealed record OpsRunningExecutionDto(
    Guid ExecutionId,
    Guid WorkflowId,
    string Status,
    DateTime StartedAt,
    Guid? ParentExecutionId);

public sealed record OpsRecentExecutionDto(
    Guid ExecutionId,
    Guid WorkflowId,
    string Status,
    DateTime StartedAt,
    DateTime CompletedAt,
    Guid? ParentExecutionId);
