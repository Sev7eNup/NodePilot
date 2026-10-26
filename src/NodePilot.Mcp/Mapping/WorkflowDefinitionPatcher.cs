using System.Text.Json;
using System.Text.Json.Nodes;
using NodePilot.Core.WorkflowDefinitions;

namespace NodePilot.Mcp.Mapping;

/// <summary>
/// Applies a list of node/edge operations to a workflow definition, merge-by-id, atomically.
/// Adapts the API's chat-assistant merge (<c>WorkflowDefinitionMerge</c>): an upsert keeps the
/// caller's changes but backfills untouched fields (position, handles, conditionExpression, …)
/// from the existing item, and ALWAYS protects secret config keys — an existing secret is
/// restored from the original, and a secret the caller invents on a new/changed node is rejected
/// (masked to "***") with a note. The caller validates the whole result before persisting, so no
/// invalid intermediate state is ever saved.
/// </summary>
public static class WorkflowDefinitionPatcher
{
    private const string SecretMask = "***";

    public sealed record PatchResult(JsonObject Definition, IReadOnlyList<string> Notes);

    public sealed record PatchOp(string Op, JsonObject? Node, JsonObject? Edge, string? Id);

    /// <summary>Parse the tool's <c>operations</c> JSON array into typed ops (throws on malformed
    /// input).</summary>
    public static List<PatchOp> ParseOps(JsonElement operations)
    {
        if (operations.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("operations must be a JSON array of {op, node?, edge?, id?}.");

        var ops = new List<PatchOp>();
        foreach (var el in operations.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("op", out var opEl) || opEl.ValueKind != JsonValueKind.String)
                throw new ArgumentException("each operation needs a string 'op' (upsertNode|deleteNode|upsertEdge|deleteEdge).");
            var op = opEl.GetString()!;
            var node = el.TryGetProperty("node", out var n) && n.ValueKind == JsonValueKind.Object ? (JsonObject?)JsonNode.Parse(n.GetRawText()) : null;
            var edge = el.TryGetProperty("edge", out var e) && e.ValueKind == JsonValueKind.Object ? (JsonObject?)JsonNode.Parse(e.GetRawText()) : null;
            var id = el.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
            ops.Add(new PatchOp(op, node, edge, id));
        }
        return ops;
    }

    public static PatchResult Apply(JsonElement current, IReadOnlyList<PatchOp> ops)
    {
        var notes = new List<string>();
        var nodes = ToObjectList(current, "nodes");
        var edges = ToObjectList(current, "edges");

        foreach (var op in ops)
        {
            switch (op.Op)
            {
                case "upsertNode":
                    UpsertById(nodes, RequireItem(op.Node, "node", op.Op), notes);
                    break;
                case "deleteNode":
                {
                    var id = RequireId(op.Id, op.Op);
                    nodes.RemoveAll(n => IdOf(n) == id);
                    // Drop edges incident to the removed node.
                    edges.RemoveAll(e => StringProp(e, "source") == id || StringProp(e, "target") == id);
                    break;
                }
                case "upsertEdge":
                    UpsertById(edges, RequireItem(op.Edge, "edge", op.Op), notes);
                    break;
                case "deleteEdge":
                {
                    var id = RequireId(op.Id, op.Op);
                    edges.RemoveAll(e => IdOf(e) == id);
                    break;
                }
                default:
                    throw new ArgumentException($"unknown op '{op.Op}' (expected upsertNode|deleteNode|upsertEdge|deleteEdge).");
            }
        }

        var def = new JsonObject
        {
            ["nodes"] = new JsonArray(nodes.Select(n => (JsonNode)n).ToArray()),
            ["edges"] = new JsonArray(edges.Select(e => (JsonNode)e).ToArray()),
        };
        return new PatchResult(def, notes);
    }

    /// <summary>
    /// Merge a FULL proposed definition onto the current one, by id. Used by publish/update where
    /// the caller sends the whole graph: missing ids are deletions, new ids are added, and every
    /// item's secret keys are restored from the original. Critical because the agent only ever
    /// SEES redacted definitions — a naive full-write would overwrite real secrets with "***".
    /// </summary>
    public static PatchResult MergeFull(JsonElement original, JsonElement proposed)
    {
        var notes = new List<string>();
        var originalNodes = IndexById(original, "nodes");
        var originalEdges = IndexById(original, "edges");

        var nodes = MergeFullArray(proposed, "nodes", originalNodes, notes);
        var edges = MergeFullArray(proposed, "edges", originalEdges, notes);

        var def = new JsonObject { ["nodes"] = nodes, ["edges"] = edges };
        return new PatchResult(def, notes);
    }

    private static JsonArray MergeFullArray(JsonElement proposed, string arrayName, Dictionary<string, JsonObject> originalById, List<string> notes)
    {
        var result = new JsonArray();
        if (proposed.ValueKind != JsonValueKind.Object || !proposed.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || JsonNode.Parse(item.GetRawText()) is not JsonObject obj) continue;
            JsonObject? source = null;
            var id = IdOf(obj);
            if (id is not null) originalById.TryGetValue(id, out source);
            MergeObject(obj, source, notes, id);
            result.Add(obj);
        }
        return result;
    }

    private static Dictionary<string, JsonObject> IndexById(JsonElement def, string arrayName)
    {
        var map = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (def.ValueKind == JsonValueKind.Object && def.TryGetProperty(arrayName, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object && JsonNode.Parse(item.GetRawText()) is JsonObject obj && IdOf(obj) is { } id)
                    map[id] = obj;
        return map;
    }

    private static void UpsertById(List<JsonObject> list, JsonObject proposed, List<string> notes)
    {
        var id = IdOf(proposed)
            ?? throw new ArgumentException("upsert item must have a non-empty string 'id'.");
        var existing = list.FirstOrDefault(x => IdOf(x) == id);
        var merged = (JsonObject)proposed.DeepClone();
        MergeObject(merged, existing, notes, id);
        if (existing is null)
            list.Add(merged);
        else
            list[list.IndexOf(existing)] = merged;
    }

    // Backfill untouched fields from the original + protect secret keys (adapted from the API
    // merge).
    private static void MergeObject(JsonObject target, JsonObject? source, List<string> notes, string? itemId)
    {
        if (source is not null)
            foreach (var (key, sourceVal) in source)
                if (!target.ContainsKey(key))
                    target[key] = sourceVal?.DeepClone();

        foreach (var key in target.Select(kv => kv.Key).ToList())
        {
            var targetVal = target[key];
            var sourceChild = source?[key];

            // Universal redaction round-trip: a proposed "***" means "unchanged / was redacted" for
            // ANY key, so the original is restored whatever its JSON kind. Covers inline secrets
            // masked by CONTENT (a restApi headers string, body, or script) whose config key is not
            // itself in SecretConfigKeys.
            if (IsMask(targetVal))
            {
                if (sourceChild is not null && !IsMask(sourceChild))
                    target[key] = sourceChild.DeepClone();
                else if (sourceChild is null)
                    RejectMaskWithoutOriginal(key, itemId, notes);
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

    // Named secrets keep the existing real value of any JSON kind; a new or differing value from
    // the agent is rejected and noted.
    private static void ReconcileNamedSecret(
        JsonObject target, string key, JsonNode? proposed, JsonNode? original, List<string> notes)
    {
        var originalReal = HasValue(original) && !IsMask(original) ? original : null;
        var proposedReal = HasValue(proposed) ? proposed : null;

        if (originalReal is not null)
        {
            target[key] = originalReal.DeepClone();
            if (proposedReal is not null && !JsonNode.DeepEquals(proposedReal, originalReal))
                notes.Add($"Secret '{key}' not changed — kept the original value (set secrets manually on the node).");
        }
        else if (proposedReal is not null)
        {
            target[key] = JsonValue.Create(SecretMask);
            notes.Add($"Secret '{key}' must be set manually — the agent may not assign secret values.");
        }
    }

    // A mask with nothing to restore would be saved as the literal "***", for example a condition
    // operand copied onto a new edge, so the whole operation is refused. A named secret keeps the
    // mask with a note, as when the agent invents a secret value.
    private static void RejectMaskWithoutOriginal(string key, string? itemId, List<string> notes)
    {
        if (WorkflowSecretKeys.SecretConfigKeys.Contains(key))
        {
            notes.Add($"Secret '{key}' must be set manually — the agent may not assign secret values.");
            return;
        }
        throw new ArgumentException(
            $"'{key}' on '{itemId ?? "?"}' is '***' but has no stored value to restore. Masked values cannot be copied onto new nodes or edges; send the real value instead. Nothing was saved.");
    }

    // Null and the empty string count as "no secret set".
    private static bool HasValue(JsonNode? node)
        => node is not null && !(node is JsonValue value && value.TryGetValue(out string? text) && string.IsNullOrEmpty(text));

    private static bool IsMask(JsonNode? node)
        => node is JsonValue value && value.TryGetValue(out string? text) && text == SecretMask;

    // Nested condition/case arrays have no stable item identity. If a redacted array still
    // contains a mask, retain the whole original array so a reorder cannot attach a secret to the
    // wrong case. Otherwise recurse by index to keep named-secret protection for nested objects.
    private static void MergeNestedArray(
        JsonArray target, JsonArray? source, List<string> notes, string? itemId, string key)
    {
        if (source is not null && ContainsMask(target))
        {
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
                case JsonValue targetValue when targetValue.TryGetValue(out string? value)
                                               && value == SecretMask:
                    if (sourceItem is not null) target[index] = sourceItem.DeepClone();
                    else RejectMaskWithoutOriginal(key, itemId, notes);
                    break;
            }
        }
    }

    private static bool ContainsMask(JsonNode node)
    {
        if (node is JsonValue value)
            return value.TryGetValue(out string? text) && text == SecretMask;
        if (node is JsonObject obj)
            return obj.Any(property => property.Value is not null && ContainsMask(property.Value));
        return node is JsonArray array
               && array.Any(item => item is not null && ContainsMask(item));
    }

    private static List<JsonObject> ToObjectList(JsonElement def, string arrayName)
    {
        var list = new List<JsonObject>();
        if (def.ValueKind == JsonValueKind.Object && def.TryGetProperty(arrayName, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object && JsonNode.Parse(item.GetRawText()) is JsonObject obj)
                    list.Add(obj);
        return list;
    }

    private static string? IdOf(JsonObject obj)
        => obj["id"] is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrEmpty(s) ? s : null;

    private static string? StringProp(JsonObject obj, string name)
        => obj[name] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static JsonObject RequireItem(JsonObject? item, string field, string op)
        => item ?? throw new ArgumentException($"op '{op}' requires a '{field}' object.");

    private static string RequireId(string? id, string op)
        => string.IsNullOrEmpty(id) ? throw new ArgumentException($"op '{op}' requires an 'id'.") : id;
}
