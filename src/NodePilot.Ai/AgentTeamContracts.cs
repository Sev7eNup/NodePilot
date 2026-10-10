namespace NodePilot.Ai;

/// <summary>
/// Names the drafting model may refer to. Built by the API host from the caller's visible
/// resources; carries names only (no ids, usernames or secrets). The model answers with
/// names, and the host resolves them back to ids.
/// </summary>
public sealed record AgentTeamInventory(
    IReadOnlyList<AgentTeamInventoryMachine> Machines,
    IReadOnlyList<string> Credentials,
    IReadOnlyList<AgentTeamInventorySkill> Skills,
    IReadOnlyList<AgentTeamInventoryMcpServer> McpServers,
    IReadOnlyList<string> Workflows,
    bool ServiceIdentityAvailable);

public sealed record AgentTeamInventoryMachine(string Name, string Hostname);

public sealed record AgentTeamInventorySkill(string Name, string Version, string Description);

/// <summary>An enabled MCP server with the tools that currently hold a valid read approval.</summary>
public sealed record AgentTeamInventoryMcpServer(string Name, IReadOnlyList<string> Tools);

/// <summary>One tool selection as the model proposed it: names only, nothing resolved yet.</summary>
public sealed record AgentTeamDraftTool(
    string Name,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> Hosts,
    IReadOnlyList<string> Workflows,
    string? McpServer,
    string? McpTool);

public sealed record AgentTeamDraftSkill(string Name, string? Version);

/// <summary>One team member as the model proposed it. Machine and credential are names.</summary>
public sealed record AgentTeamDraftMember(
    string Id,
    string Role,
    string Instructions,
    bool IsSupervisor,
    bool IsReviewer,
    string? Machine,
    string? Credential,
    bool ServiceIdentity,
    IReadOnlyList<AgentTeamDraftSkill> Skills,
    IReadOnlyList<AgentTeamDraftTool> Tools);

public sealed record AgentTeamDraft(
    string? Task,
    int? MaxParallelMembers,
    IReadOnlyList<AgentTeamDraftMember> Members);

public sealed record AgentTeamDraftResult(
    AgentTeamDraft Draft,
    bool Retried,
    int DurationMs,
    string Model,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens);
