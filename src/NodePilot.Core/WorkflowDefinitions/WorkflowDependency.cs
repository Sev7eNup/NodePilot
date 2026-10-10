using System.Text.Json.Nodes;

namespace NodePilot.Core.WorkflowDefinitions;

/// <summary>Portable resource identity, never resource configuration or credentials.</summary>
public sealed record WorkflowDependency(
    string Kind, Guid SourceId, string? Name,
    string? Identity = null, string? Version = null, string? Sha256 = null,
    Guid? TargetId = null);

/// <summary>Only known reference slots are visited; arbitrary tool arguments are not rewritten.</summary>
public static class WorkflowResourceReferences
{
    public sealed record Reference(string Kind, Guid Id, string Path, Action<Guid> Replace);

    public static IEnumerable<Reference> Enumerate(JsonNode? definition)
    {
        if (definition is not JsonObject root || root["nodes"] is not JsonArray nodes) yield break;
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is not JsonObject node || node["data"] is not JsonObject data) continue;
            var path = $"nodes[{i}].data";
            foreach (var r in Binding(data, path)) yield return r;
            if (data["config"] is not JsonObject config) continue;
            var type = Text(data["activityType"]) ?? Text(node["type"]);
            if (type == "startWorkflow")
                foreach (var r in Scalar(config, "workflowNameOrId", "workflow", path + ".config")) yield return r;
            if (type == "forEach")
                foreach (var r in Scalar(config, "childWorkflowNameOrId", "workflow", path + ".config")) yield return r;
            if (type is not ("aiAgent" or "aiAgentTeam")) continue;
            if (config["agent"] is JsonObject agent)
                foreach (var r in Agent(agent, path + ".config.agent")) yield return r;
            if (config["members"] is JsonArray members)
                for (var m = 0; m < members.Count; m++)
                    if (members[m] is JsonObject member)
                        foreach (var r in Agent(member, $"{path}.config.members[{m}]")) yield return r;
        }
    }

    public static string? UnresolvedError(string json)
    {
        try
        {
            var missing = Enumerate(JsonNode.Parse(json)).FirstOrDefault(r => r.Id == Guid.Empty);
            return missing is null ? null : $"Select the unresolved imported {missing.Kind} at {missing.Path} before publishing or enabling.";
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static IEnumerable<Reference> Agent(JsonObject agent, string path)
    {
        foreach (var r in Binding(agent, path)) yield return r;
        foreach (var r in Array(agent, "skillIds", "skill", path)) yield return r;
        if (agent["tools"] is not JsonArray tools) yield break;
        for (var i = 0; i < tools.Count; i++)
        {
            if (tools[i] is not JsonObject tool) continue;
            if (Text(tool["name"]) == "mcp")
                foreach (var r in Scalar(tool, "mcpServerId", "mcpServer", $"{path}.tools[{i}]")) yield return r;
            if (Text(tool["name"]) == "workflow_run")
                foreach (var r in Array(tool, "workflowIds", "workflow", $"{path}.tools[{i}]")) yield return r;
        }
    }

    private static IEnumerable<Reference> Binding(JsonObject obj, string path)
    {
        foreach (var r in Scalar(obj, "targetMachineId", "machine", path)) yield return r;
        foreach (var r in Scalar(obj, "credentialId", "credential", path)) yield return r;
    }

    private static IEnumerable<Reference> Scalar(JsonObject obj, string key, string kind, string path)
    {
        if (Guid.TryParse(Text(obj[key]), out var id))
            yield return new(kind, id, path + "." + key, replacement => obj[key] = replacement.ToString());
    }

    private static IEnumerable<Reference> Array(JsonObject obj, string key, string kind, string path)
    {
        if (obj[key] is not JsonArray array) yield break;
        for (var i = 0; i < array.Count; i++)
        {
            var index = i;
            if (Guid.TryParse(Text(array[i]), out var id))
                yield return new(kind, id, $"{path}.{key}[{i}]", replacement => array[index] = replacement.ToString());
        }
    }

    private static string? Text(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
