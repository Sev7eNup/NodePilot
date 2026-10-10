using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodePilot.Ai;
using NodePilot.Core.Agents;
using NodePilot.Core.Interfaces;
using NodePilot.Data;
using NodePilot.Engine.Agents;

namespace NodePilot.Api.Ai;

public sealed record InventoryMachine(Guid Id, string Name, string Hostname);
public sealed record InventoryNamed(Guid Id, string Name);
public sealed record InventorySkill(Guid Id, string Name, string Version, string Description);
public sealed record InventoryMcpServer(Guid Id, string Name, IReadOnlyList<string> Tools);

/// <summary>
/// The resources a caller may bind to a drafted team, with ids. The model only gets the name
/// view (<see cref="ToPromptInventory"/>); the resolver maps names back to these ids.
/// </summary>
public sealed record AgentTeamResolvableInventory(
    IReadOnlyList<InventoryMachine> Machines,
    IReadOnlyList<InventoryNamed> Credentials,
    IReadOnlyList<InventorySkill> Skills,
    IReadOnlyList<InventoryMcpServer> McpServers,
    IReadOnlyList<InventoryNamed> Workflows,
    bool ServiceIdentityAvailable)
{
    public AgentTeamInventory ToPromptInventory() => new(
        Machines.Select(m => new AgentTeamInventoryMachine(m.Name, m.Hostname)).ToList(),
        Credentials.Select(c => c.Name).ToList(),
        Skills.Select(s => new AgentTeamInventorySkill(s.Name, s.Version, s.Description)).ToList(),
        McpServers.Select(s => new AgentTeamInventoryMcpServer(s.Name, s.Tools)).ToList(),
        Workflows.Select(w => w.Name).ToList(),
        ServiceIdentityAvailable);
}

/// <summary>
/// Collects the bindable resources for one caller. Workflows are limited to those the agent
/// runtime would accept (enabled, unlocked, published) in folders the caller may run; MCP tools
/// to approvals that match the server's current revision. The runtime still re-checks the
/// tool contract, so listing here is not a guarantee that a call will succeed.
/// </summary>
public sealed class AgentTeamInventoryBuilder(
    NodePilotDbContext db,
    IResourceAuthorizationService authz,
    AgentExternalReadPolicy mcpPolicy,
    IOptionsMonitor<AgentOptions> agentOptions)
{
    private const int MaxPerKind = 500;

    public async Task<AgentTeamResolvableInventory> BuildAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var machines = await db.ManagedMachines.AsNoTracking().OrderBy(m => m.Name).Take(MaxPerKind)
            .Select(m => new InventoryMachine(m.Id, m.Name, m.Hostname)).ToListAsync(ct);
        var credentials = await db.Credentials.AsNoTracking().OrderBy(c => c.Name).Take(MaxPerKind)
            .Select(c => new InventoryNamed(c.Id, c.Name)).ToListAsync(ct);
        var skills = await db.AgentSkillPackages.AsNoTracking().Where(s => s.Enabled).OrderBy(s => s.Name).Take(MaxPerKind)
            .Select(s => new InventorySkill(s.Id, s.Name, s.Version, s.Description)).ToListAsync(ct);

        var options = agentOptions.CurrentValue;
        var servers = await db.AgentMcpServers.AsNoTracking().Where(s => s.Enabled).OrderBy(s => s.Name).Take(MaxPerKind).ToListAsync(ct);
        var mcp = servers
            .Select(s => new InventoryMcpServer(s.Id, s.Name, (options.ReadOnlyMcpTools ?? [])
                .Where(g => g is not null).Select(g => g.ToolName).Distinct(StringComparer.Ordinal)
                .Where(tool => mcpPolicy.FindGrant(s, tool) is not null).ToList()))
            .Where(s => s.Tools.Count > 0).ToList();

        return new AgentTeamResolvableInventory(machines, credentials, skills, mcp,
            await RunnableWorkflowsAsync(user, ct),
            options.AllowServiceIdentity && user.IsInRole("Admin"));
    }

    private async Task<IReadOnlyList<InventoryNamed>> RunnableWorkflowsAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var candidates = await db.Workflows.AsNoTracking()
            .Where(w => w.IsEnabled && w.CheckedOutByUserId == null && w.PublishedByUserId != null)
            .OrderBy(w => w.Name).Take(MaxPerKind)
            .Select(w => new { w.Id, w.Name, w.FolderId }).ToListAsync(ct);

        var allowedFolders = new Dictionary<Guid, bool>();
        var result = new List<InventoryNamed>();
        foreach (var w in candidates)
        {
            if (!allowedFolders.TryGetValue(w.FolderId, out var allowed))
            {
                allowed = await authz.CanAccessWorkflowAsync(user, w.FolderId, ResourceOp.Run, ct);
                allowedFolders[w.FolderId] = allowed;
            }
            if (allowed) result.Add(new InventoryNamed(w.Id, w.Name));
        }
        return result;
    }
}
