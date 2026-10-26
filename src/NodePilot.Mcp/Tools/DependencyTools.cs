using System.ComponentModel;
using ModelContextProtocol.Server;
using NodePilot.Mcp.Api;
using NodePilot.Mcp.Api.Dtos;
using NodePilot.Mcp.Mapping;

namespace NodePilot.Mcp.Tools;

[McpServerToolType]
public sealed class DependencyTools(NodePilotApiClient api)
{
    [McpServerTool(Name = "list_dependency_objects", ReadOnly = true)]
    [Description("Find visible workflows, machines, credentials, variables or custom activities for dependency analysis, with usedByDirect, usedByIndirect and problems counts per object. state: problems (unresolved, ambiguous or invalid references) or unused (no statically detected use; workflows excluded; not proof that deletion is safe). sort: name or usage. No secret values. Follow nextCursor for further pages.")]
    public Task<DependencyObjectsDto> ListObjects(string? query = null, string? kind = null, string? state = null, string sort = "name",
        string? cursor = null, int limit = 50, CancellationToken cancellationToken = default)
        => ApiErrorMapper.Guard(() => api.ListDependencyObjectsAsync(query, kind, state, sort, cursor, limit, cancellationToken));

    [McpServerTool(Name = "get_dependencies", ReadOnly = true)]
    [Description("Explain saved static dependencies for an object. kind: workflow, machine, credential, variable or customActivity. direction: uses or usedBy. Counts are unique visible objects; possible agent calls are separate. Check complete, limitations and problems. This is not proof that an object is safe to delete or that a change will cause an outage.")]
    public Task<DependencyReportDto> GetDependencies(string kind, Guid id, string direction = "usedBy", string? cursor = null,
        int limit = 50, CancellationToken cancellationToken = default)
        => ApiErrorMapper.Guard(() => api.GetDependenciesAsync(kind, id, direction, cursor, limit, cancellationToken));

    [McpServerTool(Name = "get_dependency_evidence", ReadOnly = true)]
    [Description("Read exact workflow step/configuration reference locations and up to three explanatory paths for one related object from get_dependencies. Uses the same folder and role permissions. Does not include raw configuration or secret values.")]
    public Task<DependencyEvidenceDto> GetEvidence(string kind, Guid id, string relatedKind, Guid relatedId,
        string direction = "usedBy", string? cursor = null, int limit = 50, CancellationToken cancellationToken = default)
        => ApiErrorMapper.Guard(() => api.GetDependencyEvidenceAsync(kind, id, relatedKind, relatedId, direction, cursor, limit, cancellationToken));
}
