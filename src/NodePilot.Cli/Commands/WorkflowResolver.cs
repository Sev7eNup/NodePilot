using System.Runtime.Versioning;
using NodePilot.Cli.Api;
using NodePilot.Cli.Api.Dtos;

namespace NodePilot.Cli.Commands;

/// <summary>
/// Resolves a CLI workflow argument that may be either a Guid or a name. List endpoint
/// is the only way to look up by name today (no GET /by-name route on the API).
/// </summary>
[SupportedOSPlatform("windows")]
public static class WorkflowResolver
{
    public static async Task<WorkflowResponse> ResolveAsync(NodePilotApiClient api, string idOrName, CancellationToken ct)
    {
        if (Guid.TryParse(idOrName, out var id))
            return await api.GetWorkflowAsync(id, ct);

        var all = await api.ListWorkflowsAsync(ct);
        var matches = all.Where(w => string.Equals(w.Name, idOrName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            throw new InvalidOperationException($"No workflow named '{idOrName}'.");
        if (matches.Count > 1)
            throw new InvalidOperationException($"Multiple workflows named '{idOrName}' — disambiguate with the GUID.");
        return matches[0];
    }
}
