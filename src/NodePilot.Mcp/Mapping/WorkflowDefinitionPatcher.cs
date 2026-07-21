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

    /// <summary>Parse the tool's <c>operations</c> JSON array into typed ops (throws on malformed input).</summary>
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
            if (IdOf(obj) is { } id) originalById.TryGetValue(id, out source);
            MergeObject(obj, source, notes);
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
        MergeObject(merged, existing, notes);
        if (existing is null)
            list.Add(merged);
        else
            list[list.IndexOf(existing)] = merged;
    }

    // Backfill untouched fields from the original + protect secret keys (adapted from the API merge).
    private static void MergeObject(JsonObject target, JsonObject? source, List<string> notes)
    {
        if (source is not null)
            foreach (var (key, sourceVal) in source)
                if (!target.ContainsKey(key))
                    target[key] = sourceVal?.DeepClone();

        foreach (var key in target.Select(kv => kv.Key).ToList())
        {
            var targetVal = target[key];
            var sourceChild = source?[key];

            if (targetVal is JsonObject targetObj)
            {
                MergeObject(targetObj, sourceChild as JsonObject, notes);
                continue;
            }

            var original = sourceChild is JsonValue sv && sv.TryGetValue(out string? o) && !string.IsNullOrEmpty(o) && o != SecretMask ? o : null;

            // Universal redaction round-trip: a proposed "***" means "unchanged / was redacted" for
            // ANY key — restore the original. Covers inline secrets masked by CONTENT (a restApi
            // headers string, body, or script) whose config key is not itself in SecretConfigKeys.
            var proposedIsMask = targetVal is JsonValue mv && mv.TryGetValue(out string? mm) && mm == SecretMask;
            if (proposedIsMask)
            {
                if (original is not null) target[key] = JsonValue.Create(original);
                continue;
            }

            if (!WorkflowSecretKeys.SecretConfigKeys.Contains(key)) continue;

            var proposed = targetVal is JsonValue tv && tv.TryGetValue(out string? p) && !string.IsNullOrEmpty(p) ? p : null;

            if (original is not null)
            {
                target[key] = JsonValue.Create(original);
                if (proposed is not null && proposed != original)
                    notes.Add($"Secret '{key}' not changed — kept the original value (set secrets manually on the node).");
            }
            else if (proposed is not null)
            {
                target[key] = JsonValue.Create(SecretMask);
                notes.Add($"Secret '{key}' must be set manually — the agent may not assign secret values.");
            }
        }
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
