using System.Text.Json;
using System.Text.Json.Nodes;

namespace NodePilot.Core.WorkflowDefinitions;

/// <summary>
/// Redacts the inline secret-bearing config values inside a workflow definition to <c>"***"</c>.
/// The single, Data-free redaction walk shared by every layer that must strip secrets before a
/// definition leaves the system or is surfaced to an LLM/agent: the API's
/// <c>WorkflowDefinitionSecretRewriter</c> (its <c>Redact</c> mode), the MCP server's
/// definition-redaction layer, and the AI chat assistant (which redacts the canvas before every
/// LLM call). A value is masked when <see cref="WorkflowSecretKeys.IsSecretValue"/> is true: its
/// config key is in <see cref="WorkflowSecretKeys.SecretConfigKeys"/> or its content looks
/// like an inline secret (<see cref="WorkflowSecretContent"/>: a restApi headers string, body, or
/// script). A masked value is replaced whole with <c>"***"</c>, so the redact-edit round-trip
/// stays intact via the merge layers' universal <c>"***"</c>-restore.
/// <para>
/// Free-form payloads are masked as complete values. A small global set covers unambiguously
/// opaque fields such as scripts and HTTP bodies; an activity-aware policy covers executable
/// arguments, queries, URLs, prompts, trigger defaults and similar fields without hiding unrelated
/// metadata that happens to use the same property name. Literal operands in edge conditions are
/// opaque as well, because their grammars are open-ended and an unmatched literal cannot be
/// classified as safe by a heuristic detector.
/// </para>
/// </summary>
public static class WorkflowSecretRedactor
{
    private const string Mask = WorkflowSecretMask.Value;

    private static readonly IReadOnlySet<string> OpaqueDefinitionKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "script", "body", "headers", "scorchRaw", "content",
        };

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> OpaqueActivityConfigKeys =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["startProgram"] = Keys("arguments"),
            ["aiAgent"] = Keys("task", "resultSchema"),
            ["aiAgentTeam"] = Keys("task", "resultSchema"),
            ["scheduledTask"] = Keys("arguments"),
            ["wmiQuery"] = Keys("arguments", "query", "filter"),
            ["sql"] = Keys("query", "parameters"),
            ["databaseTrigger"] = Keys("query", "parameters"),
            ["restApi"] = Keys("url", "proxyAddress"),
            ["waitForCondition"] = Keys("url"),
            ["emailNotification"] = Keys("subject"),
            ["log"] = Keys("message"),
            ["jsonQuery"] = Keys("jsonPath"),
            ["xmlQuery"] = Keys("xpath"),
            ["textFileEdit"] = Keys("replace", "matchPattern"),
            ["startWorkflow"] = Keys("parameters"),
            ["forEach"] = Keys("items", "parameters"),
            ["returnData"] = Keys("data"),
            ["registryOperation"] = Keys("value"),
            ["llmQuery"] = Keys("prompt", "systemPrompt", "baseUrl"),
            ["serviceManagement"] = Keys("binaryPath"),
            ["manualTrigger"] = Keys("parameters"),
            ["powerManagement"] = Keys("message"),
            ["eventLogTrigger"] = Keys("messagePattern"),
        };

    // Config fields of built-in activities the workflow assistant may read in clear when the active
    // LLM profile allows it. Keyed by activity type, never by key name alone, so a custom-activity
    // input called "script" stays opaque.
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AssistantReadableConfigKeys =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["runScript"] = Keys("script"),
            ["waitForCondition"] = Keys("script"),
            ["sql"] = Keys("query", "parameters"),
            ["databaseTrigger"] = Keys("query", "parameters"),
            ["startProgram"] = Keys("arguments"),
            ["scheduledTask"] = Keys("arguments"),
            ["wmiQuery"] = Keys("arguments", "query", "filter"),
        };

    /// <summary>Returns a redacted copy of <paramref name="root"/> with secret config values masked
    /// to <c>"***"</c>.</summary>
    public static JsonNode Redact(JsonElement root)
        => Redact(root, revealAssistantReadable: false);

    /// <summary>
    /// Like <see cref="Redact(JsonElement)"/>, but scripts, SQL queries and program arguments of
    /// built-in activities stay readable for the workflow assistant. Such a value is masked whole
    /// when <see cref="WorkflowSecretContent.LooksSecretInScript"/> recognises a secret in it.
    /// Every other opaque field and every named secret stays masked.
    /// </summary>
    public static JsonNode RedactForAssistant(JsonElement root)
        => Redact(root, revealAssistantReadable: true);

    private static JsonNode Redact(JsonElement root, bool revealAssistantReadable)
    {
        var node = JsonNode.Parse(root.GetRawText())
            ?? throw new InvalidOperationException("Workflow definition is not valid JSON.");
        return Walk(node, parentName: null, isHttpHeaderValue: false, activityType: null, revealAssistantReadable);
    }

    private static JsonNode Walk(
        JsonNode node,
        string? parentName,
        bool isHttpHeaderValue,
        string? activityType,
        bool revealAssistantReadable)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var result = new JsonObject();
                var isHeadersObject = string.Equals(parentName, "headers", StringComparison.OrdinalIgnoreCase);
                // Only a direct item of the top-level nodes array may establish activity context.
                // Nested payloads are user-controlled and may legitimately contain an unrelated
                // property named activityType; allowing that value to override the inherited node
                // type would bypass the activity-aware policy for the remaining config fields.
                var localActivityType = string.Equals(parentName, "nodes", StringComparison.Ordinal)
                    ? TryGetNodeActivityType(obj) ?? activityType
                    : activityType;
                var isLiteralOperand = string.Equals(
                    TryGetString(obj, "kind"), "literal", StringComparison.OrdinalIgnoreCase);
                foreach (var (name, value) in obj)
                {
                    if (value is null)
                        result[name] = null;
                    else if (revealAssistantReadable
                             && TryGetAssistantReadable(parentName, localActivityType, name, value, out var text))
                        result[name] = JsonValue.Create(WorkflowSecretContent.LooksSecretInScript(text) ? Mask : text);
                    else if (OpaqueDefinitionKeys.Contains(name)
                             || IsOpaqueActivityConfigValue(parentName, localActivityType, name, value)
                             || (isLiteralOperand && string.Equals(name, "value", StringComparison.OrdinalIgnoreCase))
                             || IsNonStringSecretValue(name, value))
                        result[name] = JsonValue.Create(Mask);
                    else
                        result[name] = Walk(value, name, isHeadersObject, localActivityType, revealAssistantReadable);
                }
                return result;
            }
            case JsonArray arr:
            {
                var result = new JsonArray();
                foreach (var item in arr)
                    result.Add(item is null ? null : Walk(item, parentName, isHttpHeaderValue, activityType, revealAssistantReadable));
                return result;
            }
            case JsonValue val when val.TryGetValue(out string? s) && s is not null:
            {
                var mask = WorkflowSecretKeys.IsSecretValue(parentName, s, isHttpHeaderValue);
                return JsonValue.Create(mask ? Mask : s);
            }
            default:
                return node.DeepClone();
        }
    }

    // A secret key whose value is a number, boolean, object or array is masked whole: the
    // string check in Walk never sees those values, and nested keys carry no secret name.
    private static bool IsNonStringSecretValue(string key, JsonNode value)
        => WorkflowSecretKeys.SecretConfigKeys.Contains(key)
           && !(value is JsonValue jsonValue && jsonValue.TryGetValue(out string? _));

    private static bool IsOpaqueActivityConfigValue(
        string? parentName,
        string? activityType,
        string key,
        JsonNode value)
    {
        if (activityType is "aiAgent" or "aiAgentTeam" && parentName is "agent" or "members"
            && key.Equals("instructions", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.Equals(parentName, "config", StringComparison.OrdinalIgnoreCase)
            || activityType is null) return false;

        if (OpaqueActivityConfigKeys.TryGetValue(activityType, out var keys) && keys.Contains(key))
            return true;

        // Custom-activity string/multiline/select inputs have definition-specific names and are
        // injected verbatim into the PowerShell runspace. Preserve only the two structural
        // identity fields; arbitrary string inputs are opaque even when their key looks harmless.
        return NodePilot.Core.Activities.CustomActivityType.IsCustomType(activityType)
               && key is not "__customDefinitionId" and not "__customKey"
               && value is JsonValue jsonValue
               && jsonValue.TryGetValue(out string? _);
    }

    // Only a string directly under a built-in node's config qualifies; objects and arrays (e.g. a
    // structured sql parameters list) stay opaque.
    private static bool TryGetAssistantReadable(
        string? parentName,
        string? activityType,
        string key,
        JsonNode value,
        out string text)
    {
        text = "";
        if (!string.Equals(parentName, "config", StringComparison.OrdinalIgnoreCase)
            || activityType is null
            || !AssistantReadableConfigKeys.TryGetValue(activityType, out var keys)
            || !keys.Contains(key)
            || value is not JsonValue jsonValue
            || !jsonValue.TryGetValue(out string? s)
            || s is null)
            return false;
        text = s;
        return true;
    }

    private static string? TryGetString(JsonObject obj, string name)
        => obj.TryGetPropertyValue(name, out var value)
           && value is JsonValue jsonValue
           && jsonValue.TryGetValue(out string? text)
            ? text
            : null;

    private static string? TryGetNodeActivityType(JsonObject obj)
    {
        if (!obj.TryGetPropertyValue("data", out var dataNode) || dataNode is not JsonObject data)
            return null;

        var explicitActivityType = TryGetString(data, "activityType");
        if (!string.IsNullOrWhiteSpace(explicitActivityType)) return explicitActivityType;

        // Valid workflow definitions may use a concrete node.type instead of
        // data.activityType. Restrict the fallback to types for which this redactor has an
        // activity policy; generic "activity" and annotation node types then remain inert.
        var concreteNodeType = TryGetString(obj, "type");
        return concreteNodeType is not null
               && (OpaqueActivityConfigKeys.ContainsKey(concreteNodeType)
                   || NodePilot.Core.Activities.CustomActivityType.IsCustomType(concreteNodeType))
            ? concreteNodeType
            : null;
    }

    private static IReadOnlySet<string> Keys(params string[] values)
        => new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
}
