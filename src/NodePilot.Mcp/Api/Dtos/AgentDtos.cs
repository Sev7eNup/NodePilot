using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace NodePilot.Mcp.Api.Dtos;

public sealed record AgentRunResponse(Guid Id, Guid WorkflowExecutionId, string StepId, string Status,
    DateTime StartedAt, DateTime? CompletedAt, string? Result, string? Error, int ModelCalls,
    int ToolCalls, int Delegations, long? InputTokens, long? OutputTokens,
    string Outcome = "unassessed", string? OutcomeReason = null);
public sealed record AgentRunEventResponse(Guid AgentRunId, long Sequence, DateTime Timestamp,
    string? MemberId, string Kind, string? ToolName, string Content);
public sealed record AgentMcpServerResponse(Guid Id, string Name, bool Enabled, string Transport,
    string? Command, string[] Arguments, string? Endpoint, bool HasSecrets, DateTime UpdatedAt);
public sealed record AgentMcpToolResponse(string Name, string? Description, JsonElement Schema, bool ReadOnly, string ContractSha256);
public sealed record AgentSkillResponse(Guid Id, string Name, string Version, string Description,
    string Sha256, bool Enabled, DateTime CreatedAt);
public sealed class SaveAgentMcpServerRequest
{
    [Required, StringLength(128)] public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    [Required, RegularExpression("^(stdio|streamableHttp)$")] public string Transport { get; set; } = "stdio";
    [StringLength(1024)] public string? Command { get; set; }
    [Required, MaxLength(64)] public string[] Arguments { get; set; } = [];
    [StringLength(2048)] public string? Endpoint { get; set; }
    public Dictionary<string, string>? Secrets { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
public sealed class ImportAgentSkillRequest
{
    [Required, StringLength(64), RegularExpression("^[a-zA-Z0-9._-]+$")] public string Version { get; set; } = "1.0.0";
    [Required, MaxLength(10_000_000)] public byte[] Package { get; set; } = [];
}
public sealed record SetAgentSkillEnabledRequest(bool Enabled);
