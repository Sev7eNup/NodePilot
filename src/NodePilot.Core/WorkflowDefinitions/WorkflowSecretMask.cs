using System.Text.Json.Nodes;

namespace NodePilot.Core.WorkflowDefinitions;

/// <summary>
/// Rules for the <c>"***"</c> mask that redacted workflow definitions carry. Shared by the AI chat
/// merge and the MCP patcher, which both merge a proposal built from a redacted definition back
/// onto the unredacted original.
/// </summary>
public static class WorkflowSecretMask
{
    public const string Value = "***";

    public static bool IsMask(JsonNode? node)
        => node is JsonValue value && value.TryGetValue(out string? text) && text == Value;

    public static bool ContainsMask(JsonNode? node) => node switch
    {
        JsonValue => IsMask(node),
        JsonObject obj => obj.Any(property => ContainsMask(property.Value)),
        JsonArray array => array.Any(ContainsMask),
        _ => false,
    };

    /// <summary>
    /// True when <paramref name="proposed"/> differs from <paramref name="original"/> beyond its
    /// masks: a mask matches any original value, and keys the proposal omits count as unchanged
    /// because the merge backfills them.
    /// </summary>
    public static bool ChangesOriginal(JsonNode? proposed, JsonNode? original)
    {
        if (IsMask(proposed)) return false;
        return proposed switch
        {
            JsonObject proposedObject => original is not JsonObject originalObject
                || proposedObject.Any(property =>
                    !originalObject.TryGetPropertyValue(property.Key, out var originalValue)
                    || ChangesOriginal(property.Value, originalValue)),
            JsonArray proposedArray => original is not JsonArray originalArray
                || proposedArray.Count != originalArray.Count
                || proposedArray.Where((item, index) => ChangesOriginal(item, originalArray[index])).Any(),
            _ => !JsonNode.DeepEquals(proposed, original),
        };
    }
}
