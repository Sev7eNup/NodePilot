using System.Data;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Controllers;
using NodePilot.Core.Dependencies;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Operations;
using NodePilot.Data;

namespace NodePilot.Api.Services;

internal static class DependencySnapshotReader
{
    public static async Task<DependencyGraph> ReadAsync(NodePilotDbContext db, IResourceAuthorizationService authz,
        WorkflowDefinitionFactsCache cache, ClaimsPrincipal user, CancellationToken ct)
    {
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var accessible = await authz.GetAccessibleFolderIdsAsync(user, ct);
            var query = db.Workflows.AsNoTracking().ScopeToAccessibleFolders(accessible);
            var workflows = query is null ? [] : await query.Select(w => new
                { w.Id, w.Name, w.FolderId, w.UpdatedAt, w.IsEnabled }).ToListAsync(ct);
            var folders = await db.SharedWorkflowFolders.AsNoTracking().Where(f => workflows.Select(w => w.FolderId).Contains(f.Id))
                .Select(f => new { f.Id, f.Path }).ToDictionaryAsync(f => f.Id, f => f.Path, ct);
            var resources = workflows.Select(w => new DependencyResource("workflow", w.Id, w.Name,
                folders.GetValueOrDefault(w.FolderId, ""), w.IsEnabled, w.UpdatedAt.ToString("O"))).ToList();
            var machines = await db.ManagedMachines.AsNoTracking().Select(m => new
                { m.Id, m.Name, m.Hostname, m.DefaultCredentialId, m.Tags }).ToListAsync(ct);
            resources.AddRange(machines.Select(m => new DependencyResource("machine", m.Id, m.Name,
                string.Join(" · ", new[] { m.Hostname, m.Tags }.Where(s => !string.IsNullOrWhiteSpace(s))), null,
                $"{m.Hostname}|{m.DefaultCredentialId}|{m.Tags}")));
            var restricted = user.IsInRole("Admin") || user.IsInRole("Operator");
            if (restricted)
            {
                var credentials = await db.Credentials.AsNoTracking().Select(c => new { c.Id, c.Name, c.ExpiresAt }).ToListAsync(ct);
                resources.AddRange(credentials.Select(c => new DependencyResource("credential", c.Id, c.Name, "", null, c.ExpiresAt?.ToString("O") ?? "")));
                var globals = await db.GlobalVariables.AsNoTracking().Select(g => new { g.Id, g.Name, g.UpdatedAt }).ToListAsync(ct);
                resources.AddRange(globals.Select(g => new DependencyResource("variable", g.Id, g.Name, "", null, g.UpdatedAt.ToString("O"))));
            }
            var customRows = await db.CustomActivityDefinitions.AsNoTracking().Select(c => new
                { c.Id, c.Name, c.Key, c.Version, c.IsEnabled, c.RunsRemote, c.UpdatedAt }).ToListAsync(ct);
            resources.AddRange(customRows.Select(c => new DependencyResource("customActivity", c.Id, c.Name, c.Key, c.IsEnabled,
                $"{c.Version}|{c.UpdatedAt:O}|{c.RunsRemote}")));
            var facts = query is null ? new Dictionary<Guid, Core.WorkflowDefinitions.WorkflowDefinitionFacts>()
                : await cache.ResolveAsync(workflows.Select(w => (w.Id, w.UpdatedAt)).ToList(),
                    async (ids, token) => await query.Where(w => ids.Contains(w.Id))
                        .Select(w => new WorkflowDefinitionRow(w.Id, w.UpdatedAt, w.DefinitionJson)).ToListAsync(token), ct);
            var customFacts = new Dictionary<Guid, DependencyFacts>();
            foreach (var row in customRows)
                if (cache.TryGetCustomDependencies(row.Id, row.UpdatedAt, out var cached)) customFacts[row.Id] = cached;
            var stale = customRows.Where(c => !customFacts.ContainsKey(c.Id)).Select(c => c.Id).ToArray();
            if (stale.Length > 0)
            {
                var definitions = await db.CustomActivityDefinitions.AsNoTracking().Where(c => stale.Contains(c.Id))
                    .Select(c => new { c.Id, c.UpdatedAt, c.ScriptTemplate, c.InputParametersJson }).ToListAsync(ct);
                foreach (var row in definitions)
                {
                    var extracted = DependencyFacts.ExtractCustom(row.ScriptTemplate, row.InputParametersJson);
                    customFacts[row.Id] = extracted;
                    cache.StoreCustomDependencies(row.Id, row.UpdatedAt, extracted);
                }
            }
            // Only identities are read outside folder scope, to avoid claiming a different runtime target.
            // They never enter the returned resources, counts, locations or revisions.
            var identities = await db.Workflows.AsNoTracking().Select(w => new WorkflowCallGraphIdentity(w.Id, w.Name)).ToListAsync(ct);
            return DependencyGraphBuilder.Build(resources, identities, facts.ToDictionary(kv => kv.Key, kv => kv.Value.Dependencies),
                machines.Select(m => new DependencyMachine(m.Id, m.Name, m.Hostname, m.DefaultCredentialId)).ToArray(),
                customRows.Select(c => new DependencyCustom(c.Id, c.Key, c.RunsRemote, customFacts.GetValueOrDefault(c.Id, DependencyFacts.Invalid))).ToArray(), restricted);
        });
    }
}
