namespace NodePilot.Api.Dtos;

public record AuditEntryResponse(
    Guid Id, DateTime Timestamp, Guid? UserId, string? Username, string Action,
    string? ResourceType, Guid? ResourceId, string? Details, string? IpAddress);

// Cursor pagination: pass the response's nextCursor back as `afterTs`+`afterId` on the next
// request to fetch the page after this row, in deterministic newest-first order. The Id half
// disambiguates rows that share a timestamp; a pure timestamp cursor would skip ties.
public record AuditCursor(DateTime Timestamp, Guid Id);

public record AuditPageResponse(IReadOnlyList<AuditEntryResponse> Items, AuditCursor? NextCursor);
