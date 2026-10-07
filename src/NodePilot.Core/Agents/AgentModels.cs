using NodePilot.Core.Models;

namespace NodePilot.Core.Agents;

public sealed class AgentRun
{
    public Guid Id { get; set; }
    public Guid WorkflowExecutionId { get; set; }
    public string StepId { get; set; } = "";
    public string Status { get; set; } = "Running";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
    public int ModelCalls { get; set; }
    public int ToolCalls { get; set; }
    public int Delegations { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public WorkflowExecution WorkflowExecution { get; set; } = null!;
}

public sealed class AgentRunEvent
{
    public Guid AgentRunId { get; set; }
    public long Sequence { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string? MemberId { get; set; }
    public string Kind { get; set; } = "";
    public string? ToolName { get; set; }
    public string Content { get; set; } = "";
    public AgentRun AgentRun { get; set; } = null!;
}

public sealed class AgentMcpServer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Transport { get; set; } = "stdio";
    public string? Command { get; set; }
    public string ArgumentsJson { get; set; } = "[]";
    public string? Endpoint { get; set; }
    public byte[]? ProtectedSecrets { get; set; }
    public string? SecretProvider { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class AgentSkillPackage
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public byte[] Package { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed record AgentProgress(string Kind, string Content, string? MemberId = null, string? ToolName = null);
public sealed record AgentEventNotification(Guid ExecutionId, string StepId, Guid AgentRunId,
    long Sequence, DateTime Timestamp, string Kind, string Content, string? MemberId, string? ToolName);
