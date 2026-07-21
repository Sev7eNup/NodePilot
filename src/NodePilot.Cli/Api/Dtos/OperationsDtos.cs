namespace NodePilot.Cli.Api.Dtos;

// CLI-side mirror of NodePilot.Api.Dtos.OperationsGraphDto (no ProjectReference per convention).

public sealed record OperationsGraphResponse(
    IReadOnlyList<OpsNodeDto> Nodes,
    IReadOnlyList<OpsEdgeDto> Edges,
    IReadOnlyList<OpsRunningExecutionDto> Running,
    IReadOnlyList<OpsRecentExecutionDto> Recent,
    OpsCapabilitiesDto Capabilities);

public sealed record OpsNodeDto(
    Guid WorkflowId,
    string Name,
    Guid FolderId,
    string FolderPath,
    bool IsEnabled,
    int RunningCount,
    string? LastStatus,
    int? CallFrequency);

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

public sealed record OpsCapabilitiesDto(bool CanCancel);
