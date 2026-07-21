using System.Text.Json;
using System.Text.Json.Nodes;

namespace NodePilot.Core.WorkflowDefinitions;

/// <summary>
/// Redacts the inline secret-bearing config values inside a workflow definition to <c>"***"</c>.
/// The single, Data-free redaction walk shared by every layer that must strip secrets before a
/// definition leaves the system or is surfaced to an LLM/agent: the API's
/// <c>WorkflowDefinitionSecretRewriter</c> (its <c>Redact</c> mode), the MCP server's
/// definition-redaction layer, and the AI chat assistant (which redacts the canvas before every
/// LLM call). A value is masked when <see cref="WorkflowSecretKeys.IsSecretValue"/> is true — its
/// config key is in <see cref="WorkflowSecretKeys.SecretConfigKeys"/> <b>or</b> its content looks
/// like an inline secret (<see cref="WorkflowSecretContent"/>: a restApi headers string, body, or
/// script). A masked value is replaced <b>whole</b> with <c>"***"</c>, so the redact→edit
/// round-trip stays intact via the merge layers' universal <c>"***"</c>-restore.
/// </summary>
public static class WorkflowSecretRedactor
{
    private const string Mask = "***";

    /// <summary>Returns a redacted copy of <paramref name="root"/> with secret config values masked to <c>"***"</c>.</summary>
    public static JsonNode Redact(JsonElement root)
    {
        var node = JsonNode.Parse(root.GetRawText())
            ?? throw new InvalidOperationException("Workflow definition is not valid JSON.");
        return Walk(node, parentName: null);
    }

    private static JsonNode Walk(JsonNode node, string? parentName)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var (name, value) in obj)
                    result[name] = value is null ? null : Walk(value, name);
                return result;
            }
            case JsonArray arr:
            {
                var result = new JsonArray();
                foreach (var item in arr)
                    result.Add(item is null ? null : Walk(item, parentName));
                return result;
            }
            case JsonValue val when val.TryGetValue(out string? s) && s is not null:
            {
                return JsonValue.Create(WorkflowSecretKeys.IsSecretValue(parentName, s) ? Mask : s);
            }
            default:
                return node.DeepClone();
        }
    }
}
