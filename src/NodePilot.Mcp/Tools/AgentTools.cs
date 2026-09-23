using System.ComponentModel;
using ModelContextProtocol.Server;
using NodePilot.Mcp.Api;
using NodePilot.Mcp.Api.Dtos;
using NodePilot.Mcp.Mapping;

namespace NodePilot.Mcp.Tools;

[McpServerToolType]
public sealed class AgentTools(NodePilotApiClient api)
{
    [McpServerTool(Name = "list_agent_runs", ReadOnly = true)]
    [Description("List agent runs for a workflow execution; uses that execution's read permission.")]
    public async Task<object> ListAgentRuns(string executionId, CancellationToken cancellationToken = default)
        => await ApiErrorMapper.Guard(() => api.ListAgentRunsAsync(ExecutionTools.ParseGuid(executionId, "executionId"), cancellationToken));

    [McpServerTool(Name = "get_agent_events", ReadOnly = true)]
    [Description("Read durable agent journal events after a sequence number. Page size 1–500.")]
    public async Task<object> GetAgentEvents(string runId, long after = 0, int pageSize = 200, CancellationToken cancellationToken = default)
        => await ApiErrorMapper.Guard(() => api.GetAgentEventsAsync(ExecutionTools.ParseGuid(runId, "runId"), after, pageSize, cancellationToken));

    [McpServerTool(Name = "list_agent_mcp_servers", ReadOnly = true)]
    [Description("List registered MCP servers without credentials.")]
    public async Task<object> ListAgentMcpServers(CancellationToken cancellationToken = default)
        => await ApiErrorMapper.Guard(() => api.ListAgentMcpServersAsync(cancellationToken));

    [McpServerTool(Name = "discover_agent_mcp_tools")]
    [Description("Connect to an administrator-registered MCP server and list its tools. stdio starts the fixed process.")]
    public async Task<object> DiscoverAgentMcpTools(string serverId, CancellationToken cancellationToken = default)
        => await ApiErrorMapper.Guard(() => api.GetAgentMcpToolsAsync(ExecutionTools.ParseGuid(serverId, "serverId"), cancellationToken));

    [McpServerTool(Name = "save_agent_mcp_server")]
    [Description("Create or update a fixed MCP server registration (Admin). Include updatedAt when editing. Secrets are write-only.")]
    public async Task<object> SaveAgentMcpServer(string serverId, SaveAgentMcpServerRequest request, CancellationToken cancellationToken = default)
        => await ApiErrorMapper.Guard(() => api.SaveAgentMcpServerAsync(ExecutionTools.ParseGuid(serverId, "serverId"), request, cancellationToken));

    [McpServerTool(Name = "list_agent_skills", ReadOnly = true)]
    [Description("List versioned registered skill packages and integrity hashes.")]
    public async Task<object> ListAgentSkills(CancellationToken cancellationToken = default)
        => await ApiErrorMapper.Guard(() => api.ListAgentSkillsAsync(cancellationToken));

    [McpServerTool(Name = "import_agent_skill")]
    [Description("Import a versioned ZIP package containing SKILL.md (Admin, maximum 10 MB). Package is base64.")]
    public async Task<object> ImportAgentSkill(ImportAgentSkillRequest request, CancellationToken cancellationToken = default)
        => await ApiErrorMapper.Guard(() => api.ImportAgentSkillAsync(request, cancellationToken));

    [McpServerTool(Name = "set_agent_skill_enabled")]
    [Description("Enable or disable a registered skill version (Admin).")]
    public async Task<object> SetAgentSkillEnabled(string skillId, bool enabled, CancellationToken cancellationToken = default)
    {
        await ApiErrorMapper.Guard(() => api.SetAgentSkillEnabledAsync(ExecutionTools.ParseGuid(skillId, "skillId"), new SetAgentSkillEnabledRequest(enabled), cancellationToken));
        return new { updated = true };
    }

}

