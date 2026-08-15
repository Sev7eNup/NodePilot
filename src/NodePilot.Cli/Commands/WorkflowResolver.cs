using System.Net;
using System.Runtime.Versioning;
using NodePilot.Cli.Api;
using NodePilot.Cli.Api.Dtos;
using NodePilot.Core.Clients;

namespace NodePilot.Cli.Commands;

/// <summary>
/// Resolves a CLI workflow argument that may be either a Guid or a name. The name half goes
/// through <c>GET /api/workflows/by-name/{name}</c>, so `np` resolves exactly like the engine,
/// the API and the trigger path do: exact case wins, otherwise case-insensitive, ambiguity is
/// an error. Listing and filtering client-side used to disagree with all three — two workflows
/// differing only in case were "ambiguous" to `np` while every other caller picked the exact
/// match — and it dragged the whole workflow list over the wire to resolve one name.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WorkflowResolver
{
    public static async Task<WorkflowResponse> ResolveAsync(NodePilotApiClient api, string idOrName, CancellationToken ct)
    {
        if (Guid.TryParse(idOrName, out var id))
            return await api.GetWorkflowAsync(id, ct);

        try
        {
            return await api.GetWorkflowByNameAsync(idOrName, ct);
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"No workflow named '{idOrName}'.");
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException(
                $"Multiple workflows named '{idOrName}' — disambiguate with the GUID.");
        }
    }
}
