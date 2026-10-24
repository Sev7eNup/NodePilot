namespace NodePilot.Engine.Execution;

/// <summary>
/// Precomputed ancestor sets for every node of a compiled workflow graph.
/// Walks the compiled reverse adjacency, visiting each unresolved ancestor at most once per
/// node and reusing complete ancestor sets from earlier nodes.
/// </summary>
internal static class AncestorIndex
{
    /// <summary>
    /// Maps node id -> the ids of every node that can reach it along active edges (excluding the
    /// node itself). Cycles are tolerated: the visited set stops the walk.
    /// </summary>
    public static Dictionary<string, IReadOnlySet<string>> Build(
        IReadOnlyDictionary<string, List<string>> reverseAdjacency,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = new Dictionary<string, IReadOnlySet<string>>(reverseAdjacency.Count, StringComparer.Ordinal);
        foreach (var nodeId in reverseAdjacency.Keys)
            result[nodeId] = Resolve(nodeId, reverseAdjacency, result, ct);
        return result;
    }

    private static IReadOnlySet<string> Resolve(
        string nodeId,
        IReadOnlyDictionary<string, List<string>> reverseAdjacency,
        Dictionary<string, IReadOnlySet<string>> memo,
        CancellationToken ct)
    {
        // Seed the visited set with the node itself so cycles cannot reenter it.
        var ancestors = new HashSet<string>(StringComparer.Ordinal) { nodeId };
        var pending = new Stack<string>();
        pending.Push(nodeId);
        while (pending.TryPop(out var current))
        {
            ct.ThrowIfCancellationRequested();
            if (memo.TryGetValue(current, out var cached))
            {
                foreach (var ancestor in cached)
                {
                    ct.ThrowIfCancellationRequested();
                    ancestors.Add(ancestor);
                }
                continue;
            }

            if (!reverseAdjacency.TryGetValue(current, out var parents)) continue;
            foreach (var parent in parents)
            {
                ct.ThrowIfCancellationRequested();
                if (ancestors.Add(parent)) pending.Push(parent);
            }
        }

        ancestors.Remove(nodeId);
        return ancestors;
    }
}
