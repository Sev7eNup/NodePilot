namespace NodePilot.Cli.Api.Dtos;

// CLI-side mirror of NodePilot.Api.Dtos.OperationsGraphDto (no ProjectReference per convention).

/// <param name="Density">Bucketed run counts for the stretch <see
/// cref="OperationsGraphResponse.Recent"/>
/// could not reach; empty whenever the raw list already covers the window.</param>
public sealed record OperationsGraphResponse(
    IReadOnlyList<OpsNodeDto> Nodes,
    IReadOnlyList<OpsEdgeDto> Edges,
    IReadOnlyList<OpsRunningExecutionDto> Running,
    IReadOnlyList<OpsRecentExecutionDto> Recent,
    IReadOnlyList<OpsDensityLaneDto> Density,
    OpsSnapshotMetaDto Meta);

/// <param name="OverdueSeconds">Long-running threshold (Alerting:LongRunningSeconds).</param>
/// <param name="WindowMinutes">Clamped look-back window this snapshot was built for.</param>
/// <param name="RecentSinceUtc">Left edge the caller asked for.</param>
/// <param name="OldestReturnedCompletedAt">Oldest settled run actually returned; null when
/// none.</param>
/// <param name="RecentTruncated">More settled runs existed in the window than the cap
/// returns.</param>
/// <param name="DensityBucketSeconds">Width of one density bucket; 0 when no density was
/// computed.</param>
/// <param name="DensityCapped">Density describes the newest N settled runs only, not all of
/// them.</param>
public sealed record OpsSnapshotMetaDto(
    int OverdueSeconds,
    int WindowMinutes,
    DateTime RecentSinceUtc,
    DateTime? OldestReturnedCompletedAt,
    bool RecentTruncated,
    int DensityBucketSeconds,
    bool DensityCapped);

/// <param name="WorkflowId">The lane these counts belong to.</param>
/// <param name="Buckets">Only buckets with at least one run, ascending by index.</param>
public sealed record OpsDensityLaneDto(
    Guid WorkflowId,
    IReadOnlyList<OpsDensityBucketDto> Buckets);

/// <param name="BucketIndex">Offset from <c>Meta.RecentSinceUtc</c> in
/// <c>Meta.DensityBucketSeconds</c> steps.</param>
public sealed record OpsDensityBucketDto(
    int BucketIndex,
    int Total,
    int Failed,
    int Cancelled);

/// <param name="CanRun">Caller may cancel / retry / cancel-all on this workflow (folder Run
/// right).</param>
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
