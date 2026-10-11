namespace NodePilot.Api.Dtos;

public sealed record SupportLogTailResponse(
    string? File,
    int LineCount,
    IReadOnlyList<string> Lines);

/// <summary>How much of the requested window the server actually read.</summary>
public sealed record AppLogScanInfo(
    DateTimeOffset RequestedSince,
    DateTimeOffset? CoverageStart,
    bool Complete,
    bool BudgetExhausted,
    long BytesScanned,
    int FilesSkipped,
    int FragmentsDropped,
    int UnparsedLines);

/// <summary>Counts over every scanned entry in the window, independent of filters and take.</summary>
public sealed record AppLogSummary(int Errors, int Warnings, int Total);

/// <summary>Counts of the filtered result set: <c>Matched</c> before, <c>Returned</c> after take.</summary>
public sealed record AppLogResults(int Matched, int Returned, bool Truncated);

public sealed record AppLogEntryResponse(
    DateTimeOffset Timestamp,
    string Level,
    string Source,
    string Message,
    string? Template,
    string? Details,
    bool Truncated,
    string Format);

public sealed record AppLogResponse(
    AppLogScanInfo Scan,
    AppLogSummary Summary,
    AppLogResults Results,
    IReadOnlyList<AppLogEntryResponse> Entries);

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
