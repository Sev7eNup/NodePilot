namespace NodePilot.Mcp.Api.Dtos;

public sealed record DependencyObjectDto(string Kind, Guid Id, string Name, string Context, bool? Enabled, string Revision);
public sealed record DependencyLocationDto(string SourceKind, Guid SourceId, string? NodeId, string Label,
    string Field, bool Disabled, string Revision, Guid? ViaMachineId);
public sealed record DependencyLegDto(DependencyObjectDto Source, DependencyObjectDto Target, string Relation,
    int LocationCount, IReadOnlyList<DependencyLocationDto> Locations);
public sealed record DependencyPathDto(IReadOnlyList<DependencyLegDto> Links);
public sealed record DependencyItemDto(DependencyObjectDto Object, int Distance, bool Possible, IReadOnlyList<DependencyPathDto> Paths);
public sealed record DependencyProblemDto(string Kind, string Status, DependencyLocationDto Location);
public sealed record DependencyCountsDto(int Direct, int Indirect, int Possible);
public sealed record DependencyObjectSummaryDto(DependencyObjectDto Object, int UsedByDirect, int UsedByIndirect, int Problems);
public sealed record DependencyObjectsDto(IReadOnlyList<DependencyObjectSummaryDto> Items, string AnalysisRevision, string? NextCursor);
public sealed record DependencyReportDto(DependencyObjectDto Object, string Direction, DateTime GeneratedAt,
    string AnalysisRevision, bool Complete, IReadOnlyList<string> Limitations, DependencyCountsDto Counts,
    IReadOnlyList<DependencyItemDto> Items, IReadOnlyList<DependencyProblemDto> Problems, string? NextCursor);
public sealed record DependencyEvidenceDto(string AnalysisRevision, IReadOnlyList<DependencyPathDto> Paths,
    IReadOnlyList<DependencyLocationDto> Items, int Total, string? NextCursor);
