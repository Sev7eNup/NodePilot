using System.Text.Json;
using System.Text.Json.Nodes;
using NodePilot.Core.WorkflowDefinitions;

namespace NodePilot.Ai;

/// <summary>
/// Safety core of the chat assistant: merges a definition proposed by the LLM back onto the
/// unredacted original, so the AI can neither lose data nor invent secrets. Per node/edge
/// <c>id</c>:
/// <list type="bullet">
/// <item>fields the AI omitted are carried over from the original, preserving layout
/// (<c>position</c>, <c>sourceHandle</c>/<c>targetHandle</c>, <c>parentId</c>, group/sticky
/// styles) and semantics (<c>credentialId</c>, <c>conditionExpression</c>);</item>
/// <item>secret keys (<see cref="WorkflowSecretKeys.SecretConfigKeys"/>) always take the real
/// value from the original; any different value set by the AI is replaced with <c>"***"</c> and
/// recorded as a note.</item>
/// </list>
/// IDs missing from the proposal count as deletions; new IDs are carried through, with position
/// fallback left to the caller.
/// </summary>
internal static class WorkflowDefinitionMerge
{
    internal sealed record MergeResult(JsonObject Definition, IReadOnlyList<string> Notes);

    /// <summary>
    /// Merges <paramref name="proposed"/> onto <paramref name="original"/>. Both must have
    /// <c>nodes</c> and <c>edges</c> arrays, which the caller checks. Returns the merged
    /// definition plus notes, for example about discarded secrets.
    /// </summary>
    internal static MergeResult Merge(JsonElement original, JsonElement proposed)
    {
        var notes = new List<string>();

        var originalNodesById = IndexById(original, "nodes");
        var originalEdgesById = IndexById(original, "edges");

        var mergedNodes = MergeArray(proposed, "nodes", originalNodesById, notes);
        var mergedEdges = MergeArray(proposed, "edges", originalEdgesById, notes);

        var definition = new JsonObject
        {
            ["nodes"] = mergedNodes,
            ["edges"] = mergedEdges,
        };
        return new MergeResult(definition, notes);
    }

    private static Dictionary<string, JsonObject> IndexById(JsonElement def, string arrayName)
    {
        var map = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (!def.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return map;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
            var id = idEl.GetString();
            if (string.IsNullOrEmpty(id)) continue;
            if (JsonNode.Parse(item.GetRawText()) is JsonObject obj)
                map[id] = obj;
        }
        return map;
    }

    private static JsonArray MergeArray(
        JsonElement proposed, string arrayName,
        Dictionary<string, JsonObject> originalById, List<string> notes)
    {
        var result = new JsonArray();
        if (!proposed.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (JsonNode.Parse(item.GetRawText()) is not JsonObject proposedObj)
                continue;

            JsonObject? source = null;
            string? id = null;
            if (proposedObj["id"] is JsonValue idVal && idVal.TryGetValue(out id) && id is not null)
                originalById.TryGetValue(id, out source);

            MergeObject(proposedObj, source, notes, id);
            result.Add(proposedObj);
        }
        return result;
    }

    /// <summary>
    /// Recursive merge of one object: (1) keys present in the original but missing from the
    /// proposal are carried over; (2) object-valued children are merged recursively and every
    /// secret key is reconciled.
    /// </summary>
    private static void MergeObject(JsonObject target, JsonObject? source, List<string> notes, string? itemId)
    {
        // (1) Backfill: carry over from the original whatever the proposal didn't set.
        if (source is not null)
        {
            foreach (var (key, sourceVal) in source)
            {
                if (!target.ContainsKey(key))
                    target[key] = sourceVal?.DeepClone();
            }
        }

        // (2) Merge object-valued children recursively, reconcile secret values.
        foreach (var key in target.Select(kv => kv.Key).ToList())
        {
            var targetVal = target[key];
            var sourceChild = source?[key];

            // A proposed "***" means the value was redacted and stays unchanged, for any key, so
            // the original is restored whatever its JSON kind. This covers secrets masked by
            // content (a restApi headers string, body, or script) whose config key is not itself
            // in SecretConfigKeys.
            if (WorkflowSecretMask.IsMask(targetVal))
            {
                if (sourceChild is not null && !WorkflowSecretMask.IsMask(sourceChild))
                    target[key] = sourceChild.DeepClone();
                else if (sourceChild is null)
                    NoteMaskWithoutOriginal(key, itemId, notes);
                continue;
            }

            if (WorkflowSecretKeys.SecretConfigKeys.Contains(key))
            {
                ReconcileNamedSecret(target, key, targetVal, sourceChild, notes);
                continue;
            }

            if (targetVal is JsonObject targetObj)
            {
                MergeObject(targetObj, sourceChild as JsonObject, notes, itemId);
                continue;
            }

            if (targetVal is JsonArray targetArray)
                MergeNestedArray(targetArray, sourceChild as JsonArray, notes, itemId, key);
        }
    }

    /// <summary>
    /// Named secrets always take the existing real value from the original, of any JSON kind; a
    /// new or differing value proposed by the AI is rejected and noted.
    /// </summary>
    private static void ReconcileNamedSecret(
        JsonObject target, string key, JsonNode? proposed, JsonNode? original, List<string> notes)
    {
        var originalReal = HasValue(original) && !WorkflowSecretMask.IsMask(original) ? original : null;
        var proposedReal = HasValue(proposed) ? proposed : null;

        if (originalReal is not null)
        {
            target[key] = originalReal.DeepClone();
            if (proposedReal is not null && !JsonNode.DeepEquals(proposedReal, originalReal))
                notes.Add($"Secret '{key}' wurde nicht von der KI geändert — Originalwert beibehalten; bei Bedarf manuell am Node setzen.");
        }
        else if (proposedReal is not null)
        {
            // The AI set a secret value that did not exist in the original, so discard it.
            target[key] = JsonValue.Create(WorkflowSecretMask.Value);
            notes.Add($"Secret '{key}' bitte manuell am Node setzen — die KI darf keine Secrets vergeben.");
        }
    }

    // A mask with nothing to restore would be saved as the literal "***", for example a condition
    // operand copied onto a new edge.
    private static void NoteMaskWithoutOriginal(string key, string? itemId, List<string> notes)
    {
        if (WorkflowSecretKeys.SecretConfigKeys.Contains(key))
        {
            notes.Add($"Secret '{key}' bitte manuell am Node setzen — die KI darf keine Secrets vergeben.");
            return;
        }
        notes.Add($"'{key}' von '{itemId ?? "?"}' enthält '***' ohne gespeicherten Originalwert — bitte den echten Wert im Designer setzen, sonst wird '***' wörtlich gespeichert.");
    }

    // Null and the empty string count as "no secret set".
    private static bool HasValue(JsonNode? node)
        => node is not null && !(node is JsonValue value && value.TryGetValue(out string? text) && string.IsNullOrEmpty(text));

    /// <summary>
    /// Reconciles nested arrays used by grouped conditions and decision cases. Array items have no
    /// stable identity, so a mask anywhere in the proposal restores the whole original array rather
    /// than risking a secret paired with the wrong item after a reorder. If the proposal changed
    /// the array, that change is lost, so a note asks the user to edit the node by hand. Mask-free
    /// arrays are traversed by index, so named-secret protection still applies to new objects.
    /// </summary>
    private static void MergeNestedArray(
        JsonArray target, JsonArray? source, List<string> notes, string? itemId, string key)
    {
        if (source is not null && WorkflowSecretMask.ContainsMask(target))
        {
            if (WorkflowSecretMask.ChangesOriginal(target, source))
                notes.Add($"Änderung an '{key}' von '{itemId ?? "?"}' nicht übernommen, weil der Inhalt maskierte Werte enthält — bitte manuell im Designer anpassen.");
            target.Clear();
            foreach (var item in source)
                target.Add(item?.DeepClone());
            return;
        }

        for (var index = 0; index < target.Count; index++)
        {
            var targetItem = target[index];
            var sourceItem = source is not null && index < source.Count ? source[index] : null;
            switch (targetItem)
            {
                case JsonObject targetObject:
                    MergeObject(targetObject, sourceItem as JsonObject, notes, itemId);
                    break;
                case JsonArray targetArray:
                    MergeNestedArray(targetArray, sourceItem as JsonArray, notes, itemId, key);
                    break;
                case JsonValue targetValue when WorkflowSecretMask.IsMask(targetValue):
                    if (sourceItem is not null) target[index] = sourceItem.DeepClone();
                    else NoteMaskWithoutOriginal(key, itemId, notes);
                    break;
            }
        }
    }
}
