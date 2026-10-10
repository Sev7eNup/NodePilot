using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using NodePilot.Core.Agents;

namespace NodePilot.Ai.Agents;

// The host tracks protocol completion, not the semantic correctness of a review.
internal sealed class TeamCompletionState(AgentDefinition[] members, bool requireToolEvidence = true)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, int> _objections = new(StringComparer.Ordinal);
    private readonly HashSet<string> _observations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _open = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Status, string Content)> _findings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _reviews = members.Where(m => m.IsReviewer)
        .ToDictionary(m => m.Id, _ => -1, StringComparer.Ordinal);
    private int _revision;
    private int _globalRevision;
    private readonly Dictionary<string, int> _sources = members.Where(m => !m.IsReviewer)
        .ToDictionary(m => "member:" + m.Id, _ => 0, StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _dependencies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Observations, int Attempts)> _reviewAttempts = new(StringComparer.Ordinal);
    public int ObservationCount { get { lock (_sync) return _observations.Count; } }
    public int PendingCount { get { lock (_sync) return _open.Count + _reviews.Count(r => IsStale(r.Key, r.Value)); } }
    public int Revision { get { lock (_sync) return _revision; } }

    public void InvalidateReviews() => InvalidateReviews("*");

    public void InvalidateReviews(string source)
    {
        lock (_sync)
        {
            _revision++;
            // A newly introduced question can affect even reviews that did not know it existed.
            if (source == "*" || !_sources.ContainsKey(source)) _globalRevision = _revision;
            if (source != "*") _sources[source] = _revision;
        }
    }

    private bool IsStale(string reviewer, int revision) => revision < 0 || revision < _globalRevision
        || (_dependencies.TryGetValue(reviewer, out var dependencies)
            ? dependencies.Any(source => _sources[source] > revision) : revision != _revision);

    public string[] ReviewSources() { lock (_sync) return _sources.Keys.Order(StringComparer.Ordinal).ToArray(); }

    public string HostState()
    {
        lock (_sync) return JsonSerializer.Serialize(new
        {
            revision = _revision,
            members = members.Select(m => new { memberId = m.Id,
                status = _findings.TryGetValue(m.Id, out var finding) ? finding.Status : "not_started" }),
            approvedReviews = _reviews.Where(r => !IsStale(r.Key, r.Value)).Select(r => r.Key),
            reviewRequired = _reviews.Where(r => IsStale(r.Key, r.Value)).Select(r => r.Key),
            unresolvedMembers = _open.Keys
        });
    }

    public string? CurrentReview(string memberId)
    {
        lock (_sync) return _reviews.TryGetValue(memberId, out var revision) && !IsStale(memberId, revision)
            && _findings.TryGetValue(memberId, out var finding) ? finding.Content : null;
    }

    public JsonElement GetMemberFindings(string recipient)
    {
        lock (_sync) return JsonSerializer.SerializeToElement(
        _findings.Where(x => x.Key != recipient).Select(x => new
        {
            memberId = x.Key,
            targetMachineId = members.First(m => m.Id == x.Key).TargetMachineId,
            status = x.Value.Status,
            content = x.Value.Content
        }));
    }

    public void Observe(string memberId, string tool, string input, string result)
    {
        lock (_sync)
        {
            var targetMachineId = members.FirstOrDefault(m => m.Id == memberId)?.TargetMachineId;
            var added = _observations.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { targetMachineId, tool, input, result = ObservationContent(tool, result) })))));
            if (added && !_reviews.ContainsKey(memberId)) InvalidateReviews("member:" + memberId);
        }
    }

    // Leave room for a wording correction and one reviewer-owned read before requiring progress.
    public bool TryBeginReview(string memberId)
    {
        lock (_sync)
        {
            if (!_reviews.ContainsKey(memberId)) return true;
            var attempts = _reviewAttempts.TryGetValue(memberId, out var previous) && previous.Observations == ObservationCount
                ? previous.Attempts : 0;
            if (attempts >= 3) return false;
            _reviewAttempts[memberId] = (ObservationCount, attempts + 1);
            return true;
        }
    }

    public bool CanBeginReview(string memberId)
    {
        lock (_sync) return !_reviews.ContainsKey(memberId) || !_reviewAttempts.TryGetValue(memberId, out var previous)
            || previous.Observations != _observations.Count || previous.Attempts < 3;
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

    public bool Record(string memberId, string status, string content, bool requiresNewEvidence = true, int? reviewedRevision = null, string[]? dependencies = null)
    {
        lock (_sync)
        {
            if (_reviews.ContainsKey(memberId) && dependencies is not null
                && (dependencies.Length == 0 || dependencies.Any(source => !_sources.ContainsKey(source))))
                throw new ArgumentException("Review dependencies must name existing member/check sources, or be omitted for a whole-team review.");
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
            if (_reviews.ContainsKey(memberId))
            {
                _reviews[memberId] = status == "completed" ? reviewedRevision ?? _revision : -1;
                if (dependencies is null) _dependencies.Remove(memberId);
                else _dependencies[memberId] = dependencies.Distinct(StringComparer.Ordinal).ToArray();
            }
            else InvalidateReviews("member:" + memberId);
            return true;
        }
    }

    public string? GetBlockers()
    {
        lock (_sync)
        {
            var stale = _reviews.Where(r => IsStale(r.Key, r.Value)).Select(r => r.Key).ToArray();
            if (_open.Count == 0 && stale.Length == 0) return null;
            return JsonSerializer.Serialize(new
            {
                unresolvedMembers = _open,
                reviewRequired = stale,
                globalReviewChange = stale.Any(id => _reviews[id] < _globalRevision),
                changedReviewSources = stale.ToDictionary(id => id, id => _sources
                    .Where(s => s.Value > _reviews[id] && (!_dependencies.TryGetValue(id, out var dependencies) || dependencies.Contains(s.Key)))
                    .Select(s => s.Key).ToArray()),
                evidenceRequired = _objections.Where(x => x.Value == _observations.Count).Select(x => x.Key).ToArray()
            });
        }
    }
}
