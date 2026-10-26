namespace NodePilot.Core.Dependencies;

public readonly record struct DependencyKey(string Kind, Guid Id);
public sealed record DependencyResource(string Kind, Guid Id, string Name, string Context, bool? Enabled, string Revision)
{
    public DependencyKey Key => new(Kind, Id);
}
public sealed record DependencyMachine(Guid Id, string Name, string Hostname, Guid? CredentialId);
public sealed record DependencyCustom(Guid Id, string Key, bool RunsRemote, DependencyFacts Facts);
public sealed record DependencyLocation(DependencyKey Source, string? NodeId, string Label, string Field,
    bool Disabled, string Revision, string? Via = null, string? DefaultParameter = null,
    IReadOnlyList<string>? SuppliedInputs = null);
public sealed record DependencyLink(DependencyKey Source, DependencyKey Target, string Relation,
    IReadOnlyList<DependencyLocation> Locations);
public sealed record DependencyProblem(DependencyKey Source, string Kind, string Status, DependencyLocation Location);
public sealed record DependencyPath(IReadOnlyList<DependencyLink> Links);
public sealed record DependencyMatch(DependencyKey Key, int Distance, bool Possible, IReadOnlyList<DependencyPath> Paths);
public sealed record DependencyAnalysis(IReadOnlyList<DependencyMatch> Matches, bool Complete, IReadOnlyList<string> Limitations);

public sealed class DependencyGraph(IReadOnlyList<DependencyResource> resources, IReadOnlyList<DependencyLink> links,
    IReadOnlyList<DependencyProblem> problems, IReadOnlyDictionary<DependencyKey, IReadOnlyList<string>> limitations)
{
    public IReadOnlyList<DependencyResource> Resources { get; } = resources;
    public IReadOnlyList<DependencyLink> Links { get; } = links;
    public IReadOnlyList<DependencyProblem> Problems { get; } = problems;
    public IReadOnlyDictionary<DependencyKey, IReadOnlyList<string>> Limitations { get; } = limitations;

    public DependencyAnalysis Analyze(DependencyKey root, bool incoming, int maxDepth = 12, int maxEdges = 20_000)
    {
        var adjacency = Links.GroupBy(e => incoming ? e.Target : e.Source).ToDictionary(g => g.Key, g => g.ToArray());
        var paths = new Dictionary<DependencyKey, List<DependencyPath>>();
        var queue = new Queue<(DependencyKey Key, List<DependencyLink> Path, HashSet<DependencyKey> Visited)>();
        queue.Enqueue((root, [], [root]));
        var expanded = new HashSet<(DependencyKey Key, DependencyLink Edge, bool Possible)>();
        var complete = true;
        var scanned = 0;
        while (queue.Count > 0)
        {
            var state = queue.Dequeue();
            if (!adjacency.TryGetValue(state.Key, out var candidates)) continue;
            foreach (var edge in candidates)
            {
                if (++scanned > maxEdges) { complete = false; queue.Clear(); break; }
                var next = incoming ? edge.Source : edge.Target;
                if (state.Visited.Contains(next)) continue;
                if (state.Path.Count > 0)
                {
                    var previous = incoming ? state.Path[0] : state.Path[^1];
                    if (!CanConnect(incoming ? edge : previous, incoming ? previous : edge)) continue;
                }
                if (state.Path.Count >= maxDepth) { complete = false; continue; }
                var path = incoming ? new List<DependencyLink> { edge }.Concat(state.Path).ToList()
                    : state.Path.Append(edge).ToList();
                var possible = path.Any(e => e.Relation == "mayCall");
                if (!paths.TryGetValue(next, out var found)) paths[next] = found = [];
                if (found.Count < 3 || (!possible && found.All(p => p.Links.Any(e => e.Relation == "mayCall"))))
                {
                    if (found.Count == 3) found.RemoveAt(2);
                    found.Add(new(path));
                }
                if (expanded.Add((next, edge, possible)))
                    queue.Enqueue((next, path, new(state.Visited) { next }));
            }
        }
        var matches = paths.Select(kv =>
        {
            var definite = kv.Value.Where(p => p.Links.All(e => e.Relation != "mayCall")).ToList();
            var selected = definite.Count > 0 ? definite : kv.Value;
            return new DependencyMatch(kv.Key, selected.Min(p => p.Links.Count), definite.Count == 0,
                kv.Value.OrderBy(p => p.Links.Any(e => e.Relation == "mayCall")).ThenBy(p => p.Links.Count).Select(Explain).ToArray());
        }).ToArray();
        return new(matches, complete, complete ? [] : ["analysisBudget"]);
    }

    /// <summary>Distinct objects that reference each object directly. Possible agent calls are not usage.</summary>
    public IReadOnlyDictionary<DependencyKey, int> DirectUsers() => Links
        .Where(l => l.Relation != "mayCall" && l.Source != l.Target)
        .GroupBy(l => l.Target).ToDictionary(g => g.Key, g => g.Select(l => l.Source).Distinct().Count());

    /// <summary>References that need attention: unresolved, ambiguous or unreadable configuration.</summary>
    public IReadOnlyDictionary<DependencyKey, int> ProblemCounts() => Problems
        .Where(p => p.Status is "unresolved" or "ambiguous" or "invalid")
        .GroupBy(p => p.Source).ToDictionary(g => g.Key, g => g.Count());

    private static bool CanConnect(DependencyLink left, DependencyLink right)
    {
        if (left.Relation is "calls" or "mayCall") return true;
        if (left.Relation != "usesCustom") return false;
        if (right.Relation == "usesVariable") return true;
        return right.Relation == "usesDefaultVariable" && right.Locations.Any(r =>
            left.Locations.Any(l => r.DefaultParameter is null || l.SuppliedInputs?.Contains(r.DefaultParameter, StringComparer.Ordinal) != true));
    }

    // Evidence must point only at invocations/defaults that actually contribute to this path.
    // A grouped custom-use edge can contain both overriding and inheriting invocations.
    private static DependencyPath Explain(DependencyPath path)
    {
        var links = path.Links.ToArray();
        for (var i = 0; i + 1 < links.Length; i++)
        {
            var left = links[i]; var right = links[i + 1];
            if (left.Relation != "usesCustom" || right.Relation != "usesDefaultVariable") continue;
            bool UsesDefault(DependencyLocation use, DependencyLocation value) => value.DefaultParameter is null
                || use.SuppliedInputs?.Contains(value.DefaultParameter, StringComparer.Ordinal) != true;
            links[i] = left with { Locations = left.Locations.Where(l => right.Locations.Any(r => UsesDefault(l, r))).ToArray() };
            links[i + 1] = right with { Locations = right.Locations.Where(r => left.Locations.Any(l => UsesDefault(l, r))).ToArray() };
        }
        return new(links);
    }
}
