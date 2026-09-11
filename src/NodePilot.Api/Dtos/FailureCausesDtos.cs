namespace NodePilot.Api.Dtos;

/// <summary>A redacted, normalized message (null when none was recorded), its count and latest example.</summary>
public record FailureCause(string? Message, int Count, Guid LatestExecutionId, DateTime LatestStartedAt);
public record FailureCausesResponse(int TotalFailed, List<FailureCause> Groups, int RemainingCount);
