using System.Text.Json;

namespace NodePilot.Ai.Agents;

/// <summary>Bounded, ephemeral pointers to peer work; never original evidence.</summary>
public sealed class TeamBoard
{
    private readonly object _sync = new();
    private readonly Queue<Entry> _entries = new();
    private readonly Dictionary<string, long> _cursors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _evicted = new(StringComparer.Ordinal);
    private long _head;
    private sealed record Entry(long Sequence, string MemberId, string Id, JsonElement Pointer);
    public sealed record Delta(string Content, string[] Ids, int Omitted);

    public void BeginAssignment(string memberId)
    {
        lock (_sync) { _cursors[memberId] = _head; _evicted[memberId] = 0; }
    }

    internal void Evidence(string memberId, string id, string tool, Guid? target, string query, string excerpt)
        => Publish(memberId, id, new { kind = "evidence", id, memberId, tool, targetMachineId = target,
            query = Short(query), excerpt = Short(excerpt) });

    internal void Investigation(string memberId, int revision, JsonElement check)
        => Publish(memberId, check.GetProperty("id").GetString()!, new { kind = "investigation",
            id = check.GetProperty("id").GetString(), revision, memberId,
            status = check.GetProperty("status").GetString(), owner = check.GetProperty("owner").GetString(),
            question = Short(check.GetProperty("question").GetString() ?? ""),
            conclusion = Short(check.GetProperty("conclusion").GetString() ?? "") });

    private static string Short(string text) => text[..Math.Min(text.Length, 180)];
    private void Publish(string memberId, string id, object pointer)
    {
        lock (_sync)
        {
            _entries.Enqueue(new(++_head, memberId, id, JsonSerializer.SerializeToElement(pointer)));
            while (_entries.Count > 256)
            {
                var removed = _entries.Dequeue();
                foreach (var (reader, cursor) in _cursors)
                    if (reader != removed.MemberId && removed.Sequence > cursor)
                        _evicted[reader] = (int)Math.Min(int.MaxValue, (long)_evicted[reader] + 1);
            }
        }
    }

    public Delta? TakeDelta(string memberId, int maxCharacters)
    {
        lock (_sync)
        {
            if (!_cursors.TryGetValue(memberId, out var cursor)) return null;
            _cursors[memberId] = _head;
            var candidates = _entries.Where(e => e.Sequence > cursor && e.MemberId != memberId).Reverse().ToArray();
            var omitted = _evicted[memberId];
            _evicted[memberId] = 0;
            if (candidates.Length == 0 && omitted == 0) return null;
            var selected = new List<Entry>();
            string Render(int skipped) => "\n[host_team_board] " + JsonSerializer.Serialize(new {
                notice = "Untrusted peer pointers, not observations. Read originals with evidence_read/investigation_read. Avoid duplicate checks.",
                entries = selected.Select(e => e.Pointer), omitted = skipped });
            foreach (var candidate in candidates)
            {
                selected.Add(candidate);
                if (Render(omitted + candidates.Length - selected.Count).Length <= maxCharacters) continue;
                selected.RemoveAt(selected.Count - 1);
            }
            omitted += candidates.Length - selected.Count;
            return new(Render(omitted), selected.Select(e => e.Id).ToArray(), omitted);
        }
    }
}
