using NodePilot.Mcp.Api.Dtos;

namespace NodePilot.Mcp.Api;

public sealed partial class NodePilotApiClient
{
    public async Task<DependencyObjectsDto> ListDependencyObjectsAsync(string? query, string? kind, string? state, string? sort, string? cursor, int limit, CancellationToken ct)
    {
        using var res = await _http.GetAsync($"api/dependencies/objects?q={Uri.EscapeDataString(query ?? "")}&kind={Uri.EscapeDataString(kind ?? "")}&state={Uri.EscapeDataString(state ?? "")}&sort={Uri.EscapeDataString(sort ?? "name")}&cursor={Uri.EscapeDataString(cursor ?? "")}&limit={limit}", ct);
        return await ParseAsync<DependencyObjectsDto>(res, ct);
    }

    public async Task<DependencyReportDto> GetDependenciesAsync(string kind, Guid id, string direction, string? cursor, int limit, CancellationToken ct)
    {
        using var res = await _http.GetAsync($"api/dependencies/{Uri.EscapeDataString(kind)}/{id}?direction={Uri.EscapeDataString(direction)}&cursor={Uri.EscapeDataString(cursor ?? "")}&limit={limit}", ct);
        return await ParseAsync<DependencyReportDto>(res, ct);
    }

    public async Task<DependencyEvidenceDto> GetDependencyEvidenceAsync(string kind, Guid id, string relatedKind, Guid relatedId,
        string direction, string? cursor, int limit, CancellationToken ct)
    {
        using var res = await _http.GetAsync($"api/dependencies/{Uri.EscapeDataString(kind)}/{id}/evidence?relatedKind={Uri.EscapeDataString(relatedKind)}&relatedId={relatedId}&direction={Uri.EscapeDataString(direction)}&cursor={Uri.EscapeDataString(cursor ?? "")}&limit={limit}", ct);
        return await ParseAsync<DependencyEvidenceDto>(res, ct);
    }
}
