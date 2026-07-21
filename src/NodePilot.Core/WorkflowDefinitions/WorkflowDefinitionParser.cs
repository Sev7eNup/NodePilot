using System.Text.Json;
using NodePilot.Core.Models;

namespace NodePilot.Core.WorkflowDefinitions;

public static class WorkflowDefinitionParser
{
    public static List<WorkflowNode> ParseNodes(JsonElement definition)
    {
        var nodes = new List<WorkflowNode>();
        if (!definition.TryGetProperty("nodes", out var nodesArray)
            || nodesArray.ValueKind != JsonValueKind.Array)
        {
            return nodes;
        }

        foreach (var node in nodesArray.EnumerateArray())
        {
            var data = node.GetProperty("data");
            nodes.Add(new WorkflowNode
            {
                Id = node.GetProperty("id").GetString()!,
                Type = data.TryGetProperty("activityType", out var at) ? at.GetString()! : node.GetProperty("type").GetString()!,
                Data = new WorkflowNodeData
                {
                    Label = data.TryGetProperty("label", out var l) ? l.GetString() : null,
                    OutputVariable = data.TryGetProperty("outputVariable", out var ov) && ov.ValueKind == JsonValueKind.String
                        ? ov.GetString() : null,
                    TargetMachineRaw = data.TryGetProperty("targetMachineId", out var tm) && tm.ValueKind == JsonValueKind.String
                        ? tm.GetString() : null,
                    CredentialRaw = data.TryGetProperty("credentialId", out var ci) && ci.ValueKind == JsonValueKind.String
                        ? ci.GetString() : null,
                    Config = data.TryGetProperty("config", out var c) ? c.Clone() : default,
                    Disabled = data.TryGetProperty("disabled", out var dn)
                        && dn.ValueKind == JsonValueKind.True,
                    Breakpoint = data.TryGetProperty("breakpoint", out var bp)
                        && bp.ValueKind == JsonValueKind.True,
                    BreakpointCondition = data.TryGetProperty("breakpointCondition", out var bpc)
                        && bpc.ValueKind == JsonValueKind.String
                        ? bpc.GetString() : null,
                }
            });
        }

        return nodes;
    }

    public static List<WorkflowEdge> ParseEdges(JsonElement definition)
    {
        var edges = new List<WorkflowEdge>();
        if (!definition.TryGetProperty("edges", out var edgesArray)
            || edgesArray.ValueKind != JsonValueKind.Array)
        {
            return edges;
        }

        foreach (var edge in edgesArray.EnumerateArray())
        {
            var edgeData = edge.TryGetProperty("data", out var d) ? d : default;
            edges.Add(new WorkflowEdge
            {
                Id = edge.GetProperty("id").GetString()!,
                Source = edge.GetProperty("source").GetString()!,
                Target = edge.GetProperty("target").GetString()!,
                Condition = edgeData.ValueKind == JsonValueKind.Object && edgeData.TryGetProperty("condition", out var c)
                    && c.ValueKind == JsonValueKind.String
                    ? c.GetString() : null,
                ConditionExpression = edgeData.ValueKind == JsonValueKind.Object
                    && edgeData.TryGetProperty("conditionExpression", out var ce)
                    && ce.ValueKind == JsonValueKind.Object
                    ? ce.Clone() : null,
                Label = edgeData.ValueKind == JsonValueKind.Object && edgeData.TryGetProperty("label", out var lb)
                    ? lb.GetString() : null,
                Disabled = edgeData.ValueKind == JsonValueKind.Object && edgeData.TryGetProperty("disabled", out var dis)
                    && dis.ValueKind == JsonValueKind.True,
            });
        }

        return edges;
    }
}
