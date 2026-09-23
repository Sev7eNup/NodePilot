using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using NodePilot.Core.Agents;

namespace NodePilot.Ai.Agents;

// The host tracks protocol completion, not the semantic correctness of a review.
internal sealed class TeamCompletionState(AgentDefinition[] members, bool requireToolEvidence = true)
{
    private readonly Dictionary<string, int> _objections = new(StringComparer.Ordinal);
    private readonly HashSet<string> _observations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _open = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Status, string Content)> _findings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _reviews = members.Where(m => m.IsReviewer)
        .ToDictionary(m => m.Id, _ => -1, StringComparer.Ordinal);
    private int _revision;
    private readonly Dictionary<string, (int Observations, int Attempts)> _reviewAttempts = new(StringComparer.Ordinal);
    public int ObservationCount => _observations.Count;
    public int PendingCount => _open.Count + _reviews.Count(r => r.Value != _revision);

    public void InvalidateReviews() => _revision++;

    public JsonElement GetMemberFindings(string recipient) => JsonSerializer.SerializeToElement(
        _findings.Where(x => x.Key != recipient).Select(x => new
        {
            memberId = x.Key, targetMachineId = members.First(m => m.Id == x.Key).TargetMachineId,
            status = x.Value.Status, content = x.Value.Content
        }));

    public void Observe(string memberId, string tool, string input, string result)
    {
        var targetMachineId = members.FirstOrDefault(m => m.Id == memberId)?.TargetMachineId;
        var added = _observations.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { targetMachineId, tool, input, result = ObservationContent(tool, result) })))));
        if (added && !_reviews.ContainsKey(memberId)) InvalidateReviews();
    }

    // Leave room for a wording correction and one reviewer-owned read before requiring progress.
    public bool TryBeginReview(string memberId)
    {
        if (!_reviews.ContainsKey(memberId)) return true;
        var attempts = _reviewAttempts.TryGetValue(memberId, out var previous) && previous.Observations == ObservationCount
            ? previous.Attempts : 0;
        if (attempts >= 3) return false;
        _reviewAttempts[memberId] = (ObservationCount, attempts + 1);
        return true;
    }

    private static string ObservationContent(string tool, string result)
    {
        if (tool is not ("powershell" or "cmd" or "bash")) return result;
        try
        {
            using var document = JsonDocument.Parse(result);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("stdout", out _) || !root.TryGetProperty("exitCode", out _)) return result;
            return JsonSerializer.Serialize(root.EnumerateObject()
                .Where(p => p.Name is not ("timeContext" or "durationMs"))
                .OrderBy(p => p.Name, StringComparer.Ordinal).ToDictionary(p => p.Name, p => p.Value));
        }
        catch (JsonException) { return result; }
    }

    public bool Record(string memberId, string status, string content, bool requiresNewEvidence = true)
    {
        if (!_reviews.ContainsKey(memberId) && _findings.TryGetValue(memberId, out var previous)
            && previous == (status, content)) return true;
        if (requireToolEvidence && _reviews.ContainsKey(memberId))
        {
            if (status == "completed" && _objections.TryGetValue(memberId, out var count) && count == _observations.Count)
                return false;
            if (status != "completed" && requiresNewEvidence) _objections[memberId] = _observations.Count;
            else if (status == "completed") _objections.Remove(memberId);
        }
        _findings[memberId] = (status, content);
        if (status == "completed") _open.Remove(memberId);
        else _open[memberId] = content.Length > 2000 ? content[..2000] : content;
        if (_reviews.ContainsKey(memberId)) _reviews[memberId] = status == "completed" ? _revision : -1;
        else InvalidateReviews();
        return true;
    }

    public string? GetBlockers()
    {
        var stale = _reviews.Where(r => r.Value != _revision).Select(r => r.Key).ToArray();
        if (_open.Count == 0 && stale.Length == 0) return null;
        return JsonSerializer.Serialize(new { unresolvedMembers = _open, reviewRequired = stale,
            evidenceRequired = _objections.Where(x => x.Value == _observations.Count).Select(x => x.Key).ToArray() });
    }
}
