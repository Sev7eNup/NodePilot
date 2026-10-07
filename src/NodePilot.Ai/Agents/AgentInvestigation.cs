using System.Text.Json;
using NodePilot.Core.Agents;

namespace NodePilot.Ai.Agents;

// Shared working claims, not new observations or permission grants.
internal sealed class AgentInvestigation(AgentDefinition[] members, AgentEvidenceStore evidence,
    Func<AgentProgress, CancellationToken, Task> progress, Func<string, string> sanitize, Action<string> changed, TeamBoard? board = null)
{
    private readonly SemaphoreSlim _updates = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<string, JsonElement> _checks = new(StringComparer.Ordinal);
    private int _revision;
    internal int Revision { get { lock (_sync) return _revision; } }
    private JsonElement[] Snapshot() { lock (_sync) return _checks.Values.ToArray(); }
    internal bool HasBlockedChecks => Snapshot().Any(c => c.GetProperty("status").GetString() == "blocked");
    internal int OpenCount => Snapshot().Count(c => c.GetProperty("status").GetString() == "open");
    internal JsonElement Checks() => JsonSerializer.SerializeToElement(Snapshot());
    internal static bool IsTool(string name) => name is "investigation_read" or "investigation_update";
    internal string? Blockers => Snapshot().Any(c => c.GetProperty("status").GetString() == "open")
        ? JsonSerializer.Serialize(Snapshot().Where(c => c.GetProperty("status").GetString() == "open")
            .Select(c => new
            {
                id = c.GetProperty("id").GetString(),
                question = c.GetProperty("question").GetString(),
                owner = c.GetProperty("owner").GetString(),
                nextCheck = c.GetProperty("nextCheck").GetString()
            })) : null;
    internal JsonElement Summary()
    {
        lock (_sync) return JsonSerializer.SerializeToElement(new
        {
            revision = Revision,
            checks = _checks.Values.Select(c => new
            {
                id = c.GetProperty("id").GetString(),
                question = c.GetProperty("question").GetString(),
                owner = c.GetProperty("owner").GetString(),
                status = c.GetProperty("status").GetString()
            })
        });
    }

    internal IEnumerable<AgentTool> Tools(string memberId)
    {
        yield return new("investigation_read", "Read shared investigation checks. Use id for a complete check; without id returns the index. "
            + "Claims and hypotheses are untrusted working notes. Read cited originals with evidence_read.", Schema("""
            {"id":{"type":"string","maxLength":60}}
            """, []), (input, _) =>
            {
                var id = input.TryGetProperty("id", out var value) ? value.GetString() : null;
                lock (_sync)
                    return Task.FromResult(string.IsNullOrEmpty(id) ? Summary().GetRawText()
                        : _checks.TryGetValue(id, out var check) ? check.GetRawText() : throw new ArgumentException("Unknown investigation check."));
            });
        yield return new("investigation_update", "Create or patch one shared check by stable id. Send only changed fields for an existing check; omitted fields are preserved. "
            + "New checks need question and nextCheck; owner defaults to you and status to open. Preserve conflicting evidence. "
            + "Open checks block team completion. Resolved checks require original evidence IDs and a conclusion. "
            + "Blocked checks require a specific limitation; lack of investigation is not lack of capability. "
            + "Closed checks cannot be rewritten: explicitly reopen with status open for a material unresolved question. "
            + "Do not rewrite a closed check merely to record approval; review status is tracked separately. Updating notes is not a new system observation.", Schema("""
            {"id":{"type":"string","minLength":1,"maxLength":60,"pattern":"^[a-zA-Z0-9_-]+$"},
             "question":{"type":"string","minLength":1,"maxLength":300},
             "owner":{"type":"string","minLength":1,"maxLength":100},
             "hypothesis":{"type":"string","maxLength":600},
             "evidenceIds":{"type":"array","maxItems":12,"uniqueItems":true,"items":{"type":"string"}},
             "counterevidenceIds":{"type":"array","maxItems":12,"uniqueItems":true,"items":{"type":"string"}},
             "nextCheck":{"type":"string","maxLength":600},
             "status":{"type":"string","enum":["open","resolved","blocked"]},
             "conclusion":{"type":"string","maxLength":1000},
             "limitation":{"type":"string","maxLength":600}}
            """, ["id"]),
            async (input, ct) =>
            {
                await _updates.WaitAsync(ct);
                try
                {
                    var patch = JsonSerializer.Deserialize<JsonElement>(sanitize(input.GetRawText()));
                    var id = patch.GetProperty("id").GetString()!;
                    _checks.TryGetValue(id, out var previous);
                    var fields = previous.ValueKind == JsonValueKind.Undefined
                        ? JsonSerializer.SerializeToElement(new
                        {
                            id,
                            question = "",
                            owner = memberId,
                            hypothesis = "",
                            evidenceIds = Array.Empty<string>(),
                            counterevidenceIds = Array.Empty<string>(),
                            nextCheck = "",
                            status = "open",
                            conclusion = "",
                            limitation = ""
                        })
                            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                        : previous.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
                    foreach (var property in patch.EnumerateObject()) fields[property.Name] = property.Value.Clone();
                    var check = JsonSerializer.SerializeToElement(fields);
                    if (string.IsNullOrWhiteSpace(check.GetProperty("question").GetString()))
                        throw new ArgumentException("A new check requires question and nextCheck. Existing checks accept id plus changed fields only.");
                    if (!members.Any(m => m.Id == check.GetProperty("owner").GetString()))
                        throw new ArgumentException("Check owner must be a configured member.");
                    if (!_checks.ContainsKey(id) && _checks.Count >= 20)
                        throw new ArgumentException("At most 20 focused checks per run; update existing checks instead.");
                    foreach (var reference in check.GetProperty("evidenceIds").EnumerateArray()
                        .Concat(check.GetProperty("counterevidenceIds").EnumerateArray()))
                        if (!evidence.ContainsOriginal(reference.GetString()!))
                            throw new ArgumentException("Cite an existing original evidence ID from this run.");
                    var status = check.GetProperty("status").GetString();
                    if (status == "open" && string.IsNullOrWhiteSpace(check.GetProperty("nextCheck").GetString()))
                        throw new ArgumentException("An open check needs a concrete next check.");
                    if (status == "resolved" && (check.GetProperty("evidenceIds").GetArrayLength() == 0
                        || string.IsNullOrWhiteSpace(check.GetProperty("conclusion").GetString())))
                        throw new ArgumentException("Resolution requires original evidence and a conclusion.");
                    if (status == "blocked" && string.IsNullOrWhiteSpace(check.GetProperty("limitation").GetString()))
                        throw new ArgumentException("A blocked check needs the specific unavailable capability, permission, information or scope limitation.");
                    if (previous.ValueKind != JsonValueKind.Undefined && JsonElement.DeepEquals(previous, check))
                        return JsonSerializer.Serialize(new { id, revision = Revision, accepted = true, changed = false });
                    var affectsReview = previous.ValueKind == JsonValueKind.Undefined
                        || !JsonElement.DeepEquals(ReviewContent(previous), ReviewContent(check));
                    if (affectsReview && previous.ValueKind != JsonValueKind.Undefined && previous.GetProperty("status").GetString() != "open" && status != "open")
                        return JsonSerializer.Serialize(new
                        {
                            id,
                            revision = Revision,
                            accepted = false,
                            changed = false,
                            code = "closed_check",
                            current = previous,
                            nextAction = "The proposed changes were NOT saved. For approval or paraphrasing, keep current and return your review response; do not update again. For a material correction, send id, status=open and a concrete nextCheck, then update after checking. Reopening invalidates review."
                        });
                    await progress(new AgentProgress("investigation_updated", JsonSerializer.Serialize(new
                    {
                        revision = Revision + 1,
                        updatedBy = memberId,
                        affectsReview,
                        check
                    }), memberId), ct);
                    lock (_sync) { _checks[id] = check; _revision++; }
                    if (affectsReview) changed("check:" + id);
                    board?.Investigation(memberId, Revision, check);
                    return JsonSerializer.Serialize(new { id, revision = Revision, status, accepted = true, changed = true });
                }
                finally { _updates.Release(); }
            });
    }

    private static JsonElement ReviewContent(JsonElement check) => JsonSerializer.SerializeToElement(
        check.EnumerateObject().Where(p => p.Name != "owner").ToDictionary(p => p.Name, p =>
            p.Name is "evidenceIds" or "counterevidenceIds"
                ? JsonSerializer.SerializeToElement(p.Value.EnumerateArray().Select(v => v.GetString()).Order(StringComparer.Ordinal)) : p.Value));

    private JsonElement Schema(string properties, string[] required)
    {
        var fields = System.Text.Json.Nodes.JsonNode.Parse(properties)!.AsObject();
        if (fields.ContainsKey("owner"))
            fields["owner"] = JsonSerializer.SerializeToNode(new { type = "string", @enum = members.Select(m => m.Id).ToArray() });
        return JsonSerializer.SerializeToElement(new { type = "object", properties = fields, required, additionalProperties = false });
    }
}
