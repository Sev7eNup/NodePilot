namespace NodePilot.Api.Dtos;

public sealed record SupportLogTailResponse(
    string? File,
    int LineCount,
    IReadOnlyList<string> Lines);

public sealed record SupportEventResponse(
    Guid Id,
    DateTime Timestamp,
    int Level,
    string EventType,
    string Message,
    Guid? WorkflowId,
    string? WorkflowName,
    Guid? ExecutionId,
    string? ExecutionShort,
    string? StepId,
    string? StepLabel,
    string? ActivityType,
    string? UserName,
    Guid? UserId,
    string? TraceId,
    string? SpanId,
    string? PropertiesJson);

public sealed record SupportEventCursor(DateTime AfterTs, Guid AfterId);

public sealed record SupportEventPageResponse(
    IReadOnlyList<SupportEventResponse> Items,
    SupportEventCursor? NextCursor)
{
    /// <summary>
    /// Indicates that the server has more rows beyond the returned page. With the default sort,
    /// <see cref="NextCursor"/> carries the concrete position; with a custom sort, NextCursor is
    /// null and HasMore is the only signal.
    /// </summary>
    public bool HasMore { get; init; }
}
