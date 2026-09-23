using System.Text.Json.Nodes;

namespace NodePilot.Core.Activities;

/// <summary>
/// Points the custom-activity nodes of a workflow definition at the definitions of this instance.
/// A node's <c>config.__customDefinitionId</c> is instance-local, while its key (the
/// <c>custom:&lt;key&gt;</c> activity type) is the same everywhere, so an imported workflow is
/// relinked by key.
/// </summary>
public static class CustomActivityReferences
{
    /// <summary>
    /// Rewrites <c>__customDefinitionId</c> and <c>__customKey</c> of every custom node whose key is
    /// in <paramref name="definitionIdsByKey"/>. Nodes with an unknown key keep their config, and
    /// their keys are returned in <paramref name="missingKeys"/>.
    /// </summary>
    public static string RemapByKey(
        string definitionJson,
        IReadOnlyDictionary<string, Guid> definitionIdsByKey,
        out IReadOnlyList<string> missingKeys)
    {
        var missing = new List<string>();
        missingKeys = missing;
        if (JsonNode.Parse(definitionJson) is not JsonObject root || root["nodes"] is not JsonArray nodes)
            return definitionJson;

        var changed = false;
        foreach (var node in nodes)
        {
            if (node is not JsonObject nodeObject || nodeObject["data"] is not JsonObject data) continue;
            var type = Text(data["activityType"]) ?? Text(nodeObject["type"]);
            if (!CustomActivityType.IsCustomType(type)) continue;

            var key = type![CustomActivityType.Prefix.Length..];
            if (!definitionIdsByKey.TryGetValue(key, out var definitionId))
            {
                if (!missing.Contains(key, StringComparer.Ordinal)) missing.Add(key);
                continue;
            }
            if (data["config"] is not JsonObject config)
            {
                config = new JsonObject();
                data["config"] = config;
            }
            config["__customDefinitionId"] = definitionId.ToString();
            config["__customKey"] = key;
            changed = true;
        }
        return changed ? root.ToJsonString() : definitionJson;
    }

    private static string? Text(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
