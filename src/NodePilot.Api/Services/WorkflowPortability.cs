using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Interfaces;
using NodePilot.Core.WorkflowDefinitions;
using NodePilot.Data;
using NodePilot.Api.Controllers;

namespace NodePilot.Api.Services;

internal sealed class WorkflowPortability(IReadOnlyList<WorkflowDependency> resources)
{
    // Project only portable metadata: never load passwords, server secrets or skill archives.
    internal static async Task<WorkflowPortability> LoadAsync(NodePilotDbContext db,
        IResourceAuthorizationService authz, ClaimsPrincipal user, CancellationToken ct)
    {
        var rows = new List<WorkflowDependency>();
        var machines = await db.ManagedMachines.AsNoTracking()
            .Select(x => new { x.Id, x.Name, x.Hostname, x.WinRmPort, x.UseSsl }).ToListAsync(ct);
        rows.AddRange(machines.Select(x => new WorkflowDependency("machine", x.Id, x.Name,
            FormattableString.Invariant($"{x.Hostname}:{x.WinRmPort}:{(x.UseSsl ? "https" : "http")}"))));
        rows.AddRange(await db.Credentials.AsNoTracking().Select(x => new WorkflowDependency(
            "credential", x.Id, x.Name, (x.Domain ?? "") + "\\" + x.Username, null, null, null)).ToListAsync(ct));
        rows.AddRange(await db.AgentSkillPackages.AsNoTracking().Where(x => x.Enabled).Select(x => new WorkflowDependency(
            "skill", x.Id, x.Name, null, x.Version, x.Sha256, null)).ToListAsync(ct));
        rows.AddRange(await db.AgentMcpServers.AsNoTracking().Where(x => x.Enabled).Select(x => new WorkflowDependency(
            "mcpServer", x.Id, x.Name, x.Transport, null, null, null)).ToListAsync(ct));        var accessible = await authz.GetAccessibleFolderIdsAsync(user, ct);
        var all = db.Workflows.AsNoTracking();
        var workflows = all.ScopeToAccessibleFolders(accessible) ?? all.Where(_ => false);
        rows.AddRange(await workflows.Select(x => new WorkflowDependency(
            "workflow", x.Id, x.Name, null, null, null, null)).ToListAsync(ct));
        return new(rows);
    }

    internal List<WorkflowDependency> Describe(string definition) => WorkflowResourceReferences
        .Enumerate(JsonNode.Parse(definition)).Select(r => (r.Kind, r.Id)).Distinct()
        .Select(r => resources.SingleOrDefault(x => x.Kind == r.Kind && x.SourceId == r.Id)
            ?? new WorkflowDependency(r.Kind, r.Id, null)).ToList();

    internal string Remap(string definition, IReadOnlyList<WorkflowDependency>? dependencies,
        IReadOnlyDictionary<Guid, Guid> importedWorkflows, List<string> errors, string label)
    {
        var root = JsonNode.Parse(definition);
        var declarationsById = (dependencies ?? []).Where(x => x is not null).ToLookup(x => (x.Kind, x.SourceId));
        foreach (var reference in WorkflowResourceReferences.Enumerate(root).ToList())
        {
            var declarations = declarationsById[(reference.Kind, reference.Id)].ToList();
            Guid? target = null;
            string reason = "dependency metadata is absent or duplicated";
            if (declarations.Count == 1)
            {
                var dependency = declarations[0];
                if (dependency.TargetId is { } explicitId)
                {
                    // Explicit mappings still require a visible, available target of the right kind.
                    target = resources.SingleOrDefault(x => x.Kind == reference.Kind && x.SourceId == explicitId)?.SourceId;
                    reason = "explicit target is unavailable or inaccessible";
                }
                else if (reference.Kind == "workflow" && importedWorkflows.TryGetValue(reference.Id, out var imported))
                {
                    target = imported == Guid.Empty ? null : imported;
                    reason = "the referenced workflow in this import was not created";
                }
                else
                {
                    var matches = resources.Where(x => x.Kind == reference.Kind
                        && !string.IsNullOrWhiteSpace(dependency.Name)
                        && string.Equals(x.Name, dependency.Name, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.Identity, dependency.Identity, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.Version, dependency.Version, StringComparison.Ordinal)
                        && string.Equals(x.Sha256, dependency.Sha256, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count == 1) target = matches[0].SourceId;
                    reason = matches.Count > 1 ? "multiple matching resources" : "no matching resource";
                }
            }
            reference.Replace(target ?? Guid.Empty);
            if (target is null)
                errors.Add($"{label}: unresolved {reference.Kind} '{declarations.FirstOrDefault()?.Name ?? "unknown"}' ({reference.Id}) at {reference.Path}: {reason}. Select a destination resource before publishing; imported DISABLED.");
        }
        return root!.ToJsonString();
    }
}
