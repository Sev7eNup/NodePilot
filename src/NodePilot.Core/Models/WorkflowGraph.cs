using System.Text.Json;

namespace NodePilot.Core.Models;

public class WorkflowNode
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public WorkflowNodeData Data { get; set; } = new();
}

public class WorkflowNodeData
{
    public string? Label { get; set; }
    public string? OutputVariable { get; set; }
    public string? TargetMachineRaw { get; set; }
    public string? CredentialRaw { get; set; }
    public JsonElement Config { get; set; }

    /// <summary>
    /// Author-set in the designer. Disabled nodes are omitted from the active runtime
    /// graph and from trigger/contract/metadata derivation.
    /// </summary>
    public bool Disabled { get; set; }

    /// <summary>
    /// Author-set in the designer. Only honored by debug executions; regular runs ignore it.
    /// </summary>
    public bool Breakpoint { get; set; }

    /// <summary>
    /// Optional template string evaluated before a debug pause. Empty or null means always pause.
    /// </summary>
    public string? BreakpointCondition { get; set; }
}

public class WorkflowEdge
{
    public string Id { get; set; } = "";
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string? Condition { get; set; }
    public JsonElement? ConditionExpression { get; set; }
    public string? Label { get; set; }
    public bool Disabled { get; set; }
}
