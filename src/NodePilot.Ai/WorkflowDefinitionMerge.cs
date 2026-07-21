using System.Text.Json;
using System.Text.Json.Nodes;
using NodePilot.Core.WorkflowDefinitions;

namespace NodePilot.Ai;

/// <summary>
/// Safety core of the chat assistant: merges a definition proposed by the LLM back onto the
/// (unredacted) original, so the AI can never lose data or invent secrets. Per node/edge
/// <c>id</c>:
/// <list type="bullet">
/// <item>fields the AI omitted are carried over from the original — layout (<c>position</c>,
/// <c>sourceHandle</c>/<c>targetHandle</c>, <c>parentId</c>, group/sticky styles) and semantics
/// (<c>credentialId</c>, <c>conditionExpression</c>, …) are preserved;</item>
/// <item>for secret keys (<see cref="WorkflowSecretKeys.SecretConfigKeys"/>) the rule is
/// <b>always</b>: restore the real value from the original; any different value set by the AI is
/// discarded (replaced with <c>"***"</c>) and recorded as a note.</item>
/// </list>
/// IDs missing from the proposal are treated as deletions; new IDs are carried through (position
/// fallback is handled by the caller / AI validation).
/// </summary>
internal static class WorkflowDefinitionMerge
{
    private const string SecretMask = "***";

    internal sealed record MergeResult(JsonObject Definition, IReadOnlyList<string> Notes);

    /// <summary>
    /// Merges <paramref name="proposed"/> onto <paramref name="original"/>. Both must have
    /// <c>nodes</c>/<c>edges</c> arrays (checked by the caller). Returns the merged definition
    /// plus notes (e.g. about discarded secrets).
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
            if (proposedObj["id"] is JsonValue idVal && idVal.TryGetValue(out string? id) && id is not null)
                originalById.TryGetValue(id, out source);

            MergeObject(proposedObj, source, notes);
            result.Add(proposedObj);
        }
        return result;
    }

    /// <summary>
    /// Recursive merge of one object: (1) keys present in the original but missing from the
    /// proposal are carried over; (2) each object-valued child is then merged recursively, and
    /// every secret key is reconciled.
    /// </summary>
    private static void MergeObject(JsonObject target, JsonObject? source, List<string> notes)
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

        // (2) Merge object-valued children recursively, reconcile secret scalar values.
        foreach (var key in target.Select(kv => kv.Key).ToList())
        {
            var targetVal = target[key];
            var sourceChild = source?[key];

            if (targetVal is JsonObject targetObj)
            {
                MergeObject(targetObj, sourceChild as JsonObject, notes);
                continue;
            }

            var hasOriginal = sourceChild is JsonValue sv
                && sv.TryGetValue(out string? orig)
                && !string.IsNullOrEmpty(orig)
                && orig != SecretMask
                    ? orig : null;

            // Universal redaction round-trip: a proposed "***" means "unchanged / was redacted" for
            // ANY key — restore the original. This covers inline secrets masked by CONTENT (a restApi
            // headers string, body, or script) whose config key is not itself in SecretConfigKeys.
            var proposedIsMask = targetVal is JsonValue mv
                && mv.TryGetValue(out string? mm) && mm == SecretMask;
            if (proposedIsMask)
            {
                if (hasOriginal is not null)
                    target[key] = JsonValue.Create(hasOriginal);
                continue;
            }

            if (!WorkflowSecretKeys.SecretConfigKeys.Contains(key))
                continue;

            var proposedReal = targetVal is JsonValue tv
                && tv.TryGetValue(out string? prop)
                && !string.IsNullOrEmpty(prop)
                    ? prop : null;

            // Named-secret rule: ALWAYS restore an existing real value from the original; new or
            // differing values proposed by the AI are rejected (and noted).
            if (hasOriginal is not null)
            {
                target[key] = JsonValue.Create(hasOriginal);
                if (proposedReal is not null && proposedReal != hasOriginal)
                    notes.Add($"Secret '{key}' wurde nicht von der KI geändert — Originalwert beibehalten; bei Bedarf manuell am Node setzen.");
            }
            else if (proposedReal is not null)
            {
                // The AI set a secret value that didn't exist in the original → discard it.
                target[key] = JsonValue.Create(SecretMask);
                notes.Add($"Secret '{key}' bitte manuell am Node setzen — die KI darf keine Secrets vergeben.");
            }
        }
    }
}
