using System.Net.Http.Json;
using NodePilot.Cli.Api.Dtos;

namespace NodePilot.Cli.Api;

public sealed partial class NodePilotApiClient
{
    public async Task<List<AgentRunResponse>> ListAgentRunsAsync(Guid executionId, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"api/agents/runs?executionId={executionId}", ct);
        return await ParseAsync<List<AgentRunResponse>>(response, ct);
    }

    public async Task<List<AgentRunEventResponse>> GetAgentEventsAsync(Guid runId, long after, int pageSize, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"api/agents/runs/{runId}/events?after={after}&pageSize={pageSize}", ct);
        return await ParseAsync<List<AgentRunEventResponse>>(response, ct);
    }

    public async Task<List<AgentMcpServerResponse>> ListAgentMcpServersAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/agents/mcp-servers", ct);
        return await ParseAsync<List<AgentMcpServerResponse>>(response, ct);
    }

    public async Task<List<AgentMcpToolResponse>> GetAgentMcpToolsAsync(Guid id, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"api/agents/mcp-servers/{id}/tools", ct);
        return await ParseAsync<List<AgentMcpToolResponse>>(response, ct);
    }

    public async Task<AgentMcpServerResponse> SaveAgentMcpServerAsync(Guid id, SaveAgentMcpServerRequest request, CancellationToken ct)
    {
        using var response = await _http.PutAsJsonAsync($"api/agents/mcp-servers/{id}", request, JsonOptions, ct);
        return await ParseAsync<AgentMcpServerResponse>(response, ct);
    }

    public async Task DeleteAgentMcpServerAsync(Guid id, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync($"api/agents/mcp-servers/{id}", ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task<List<AgentSkillResponse>> ListAgentSkillsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/agents/skills", ct);
        return await ParseAsync<List<AgentSkillResponse>>(response, ct);
    }

    public async Task<AgentSkillResponse> ImportAgentSkillAsync(ImportAgentSkillRequest request, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("api/agents/skills", request, JsonOptions, ct);
        return await ParseAsync<AgentSkillResponse>(response, ct);
    }

    public async Task SetAgentSkillEnabledAsync(Guid id, SetAgentSkillEnabledRequest request, CancellationToken ct)
    {
        using var response = await _http.PutAsJsonAsync($"api/agents/skills/{id}/enabled", request, JsonOptions, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task DeleteAgentSkillAsync(Guid id, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync($"api/agents/skills/{id}", ct);
        await EnsureSuccessAsync(response, ct);
    }

}
