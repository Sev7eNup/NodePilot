using System.Text.Json;
using System.Text.RegularExpressions;

namespace NodePilot.Core.Agents;

public sealed record AgentToolSelection
{
    public string Name { get; init; } = "";
    public string[] AllowedPaths { get; init; } = [];
    public string[] AllowedHosts { get; init; } = [];
    public Guid[] WorkflowIds { get; init; } = [];
    public Guid? McpServerId { get; init; }
    public string? McpToolName { get; init; }
}

public sealed record AgentDefinition
{
    public string Id { get; init; } = "agent";
    public string Role { get; init; } = "Assistant";
    public string Instructions { get; init; } = "";
    public string? Model { get; init; }
    public bool IsSupervisor { get; init; }
    public bool IsReviewer { get; init; }
    public Guid? TargetMachineId { get; init; }
    public Guid? CredentialId { get; init; }
    public bool UseServiceIdentity { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? BashPath { get; init; }
    public AgentToolSelection[] Tools { get; init; } = [];
    public Guid[] SkillIds { get; init; } = [];
}

public sealed record AgentActivityConfiguration : AgentConfigurationBase
{
    public AgentDefinition Agent { get; init; } = new();
    public AgentDefinition[] Members { get; init; } = [];
}

public abstract record AgentConfigurationBase
{
    public string Task { get; init; } = "";
    public string ResultFormat { get; init; } = "text";
    public JsonElement? ResultSchema { get; init; }
    public int? MaxModelCalls { get; init; }
    public int? MaxToolCalls { get; init; }
    public int? MaxDelegations { get; init; }
    public int? MaxParallelMembers { get; init; }
    public int? TimeoutSeconds { get; init; }
}

public sealed class AgentOptions
{
    public const string SectionName = "Agents";
    public bool Enabled { get; set; } = true;
    public bool AllowServiceIdentity { get; set; }
    public int MaxConcurrentRuns { get; set; } = 2;
    public int SingleModelCalls { get; set; } = 20;
    public int SingleToolCalls { get; set; } = 40;
    public int SingleTimeoutSeconds { get; set; } = 1200;
    public int TeamModelCalls { get; set; } = 100;
    public int TeamToolCalls { get; set; } = 500;
    public int TeamDelegations { get; set; } = 20;
    public int TeamMaxParallelMembers { get; set; } = 3;
    public int TeamTimeoutSeconds { get; set; } = 1800;
    public int ModelCallTimeoutSeconds { get; set; } = 180;
    public int ModelMaxOutputTokens { get; set; } = 250_000;
    public int MaxContextCharacters { get; set; } = 250_000;
    public int MaxToolOutputCharacters { get; set; } = 16_000;
    public int MaxResultCharacters { get; set; } = 64_000;
    public AgentMcpReadGrant[] ReadOnlyMcpTools { get; set; } = [];
    public const long MaxCollectedBytes = 250_000_000;
    public const int MaxSkillPackageBytes = 10_000_000;
}

public sealed record AgentMcpReadGrant
{
    public Guid ServerId { get; init; }
    public string ToolName { get; init; } = "";
    public string ServerUpdatedAt { get; init; } = "";
    public string ContractSha256 { get; init; } = "";
}

public static partial class AgentConfiguration
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static readonly IReadOnlySet<string> NativeTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "files_list", "files_read", "files_write", "files_search", "logs_collect", "logs_search",
        "http_request", "workflow_run", "powershell", "cmd", "bash", "mcp"
    };

    public static bool IsAgent(string type) => type is "aiAgent" or "aiAgentTeam";
    public static bool RequiresTarget(AgentDefinition agent) => agent.Tools.Any(t => t.Name is
        "files_list" or "files_read" or "files_write" or "files_search" or "logs_collect"
        or "powershell" or "cmd" or "bash");

    public static AgentActivityConfiguration Parse(JsonElement config, bool team)
    {
        if (config.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Agent configuration must be an object.");
        if (config.TryGetProperty("retry", out var retry) && retry.ValueKind != JsonValueKind.Null)
            throw new ArgumentException("Automatic retry is not supported for agent activities.");
        var value = config.Deserialize<AgentActivityConfiguration>(JsonOptions)
            ?? throw new ArgumentException("Agent configuration is required.");
        // The shared designer timeout field uses zero for the configured default.
        if (value.TimeoutSeconds == 0) value = value with { TimeoutSeconds = null };
        Validate(value, team);
        return value;
    }

    public static void Validate(AgentActivityConfiguration config, bool team)
    {
        if (string.IsNullOrWhiteSpace(config.Task)) throw new ArgumentException("Agent task is required.");
        if (config.Task.Length > 64_000) throw new ArgumentException("Agent task exceeds 64,000 characters.");
        if (config.ResultFormat is not ("text" or "json")) throw new ArgumentException("Result format must be text or json.");
        if (config.ResultFormat == "json" && config.ResultSchema?.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("JSON results require a JSON Schema.");
        if (config.MaxModelCalls is < 2) throw new ArgumentException("Agent model budget needs at least two calls: investigation and final report.");
        if (new[] { config.MaxModelCalls, config.MaxToolCalls, config.MaxDelegations, config.MaxParallelMembers, config.TimeoutSeconds }.Any(x => x is <= 0))
            throw new ArgumentException("Agent budgets must be positive.");
        if (config.Agent is null || config.Members is null) throw new ArgumentException("Agent and members cannot be null.");
        if (!team && (config.Agent.TargetMachineId.HasValue || config.Agent.CredentialId.HasValue))
            throw new ArgumentException("Individual agents use the workflow node's targetMachineId/credentialId fields; nested bindings are for team members.");
        if (team && (config.Members.Length is < 2 or > 12 || config.Members.Count(x => x?.IsSupervisor == true) != 1))
            throw new ArgumentException("A team requires 2–12 members and exactly one supervisor.");
        if (team && config.MaxParallelMembers > config.Members.Length - 1)
            throw new ArgumentException("Parallel member limit cannot exceed the number of non-supervisor members.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in team ? config.Members : [config.Agent])
        {
            if (member?.IsReviewer == true && (!team || member.IsSupervisor))
                throw new ArgumentException("A reviewer must be a non-supervisor team member.");
            if (member is null || string.IsNullOrEmpty(member.Id) || !Identifier().IsMatch(member.Id) || !ids.Add(member.Id))
                throw new ArgumentException("Agent member IDs must be unique identifiers (1–64 characters).");
            if (string.IsNullOrWhiteSpace(member.Role) || member.Role.Length > 128 || member.Instructions is null || member.Instructions.Length > 32_000)
                throw new ArgumentException("Agent role or instructions are invalid.");
            if (member.Tools is null || member.SkillIds is null || member.Tools.Length > 64 || member.SkillIds.Length > 20)
                throw new ArgumentException("Too many or invalid agent tools/skills.");
            var selected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in member.Tools)
            {
                if (tool is null || !NativeTools.Contains(tool.Name)) throw new ArgumentException("Unknown agent tool.");
                var key = tool.Name == "mcp" ? $"{tool.McpServerId}:{tool.McpToolName}" : tool.Name;
                if (!selected.Add(key)) throw new ArgumentException("Duplicate agent tool selection.");
                if (tool.Name == "mcp" && (tool.McpServerId is null || string.IsNullOrWhiteSpace(tool.McpToolName)))
                    throw new ArgumentException("MCP tools require a registered server and tool name.");
                if (tool.AllowedPaths is null || tool.AllowedHosts is null || tool.WorkflowIds is null)
                    throw new ArgumentException("Tool permission lists cannot be null.");
                if (tool.Name.StartsWith("files_", StringComparison.Ordinal) || tool.Name == "logs_collect")
                    if (tool.AllowedPaths.Length == 0) throw new ArgumentException($"{tool.Name} requires allowed paths.");
                if (tool.Name == "workflow_run" && tool.WorkflowIds.Length == 0)
                    throw new ArgumentException("Workflow tools require selected workflow IDs.");
            }
        }
    }

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}
