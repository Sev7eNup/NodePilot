namespace NodePilot.Api.Dtos;

public record DurationBucket(DateTime StartedAt, int Count, double? MedianMs, double? P95Ms);
public record DurationWorkflow(Guid Id, string Name);
public record DurationTrendResponse(List<DurationBucket> Buckets, List<DurationWorkflow> Workflows);
