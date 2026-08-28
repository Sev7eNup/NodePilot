namespace NodePilot.Engine.Execution;

/// <summary>
/// Precomputed ancestor sets for every node of a compiled workflow graph.
///
/// <para><see
/// cref="NodePilot.Core.WorkflowDefinitions.WorkflowDefinitionDocument.FindAncestorNodeIds"/>
/// rebuilds its incoming-edge index on every call, so calling it per node would be quadratic in
/// the edge count. This walks the already-compiled <c>ReverseAdjacency</c> once per node instead,
/// and memoizes each node's set so a shared prefix of the graph is traversed once rather than
/// once per descendant.</para>
/// </summary>
internal static class AncestorIndex
{
    /// <summary>
    /// Maps node id -> the ids of every node that can reach it along active edges (excluding the
    /// node itself). Cycles are tolerated: the visited set stops the walk.
    /// </summary>
    public static Dictionary<string, IReadOnlySet<string>> Build(
        IReadOnlyDictionary<string, List<string>> reverseAdjacency)
    {
        var result = new Dictionary<string, IReadOnlySet<string>>(reverseAdjacency.Count, StringComparer.Ordinal);
        foreach (var nodeId in reverseAdjacency.Keys)
            result[nodeId] = Resolve(nodeId, reverseAdjacency, result, []);
        return result;
    }

    private static IReadOnlySet<string> Resolve(
        string nodeId,
        IReadOnlyDictionary<string, List<string>> reverseAdjacency,
        Dictionary<string, IReadOnlySet<string>> memo,
        HashSet<string> inProgress)
    {
        if (memo.TryGetValue(nodeId, out var cached)) return cached;

        // Re-entering a node means the graph has a cycle. Returning empty here breaks the
        // recursion; the outer frame still contributes the parents it reached directly, so a
        // cyclic graph degrades to "the ancestors we could reach without looping" rather than
        // stack-overflowing. Cyclic graphs produce no roots and never run (see WorkflowEngine),
        // but the index must not blow up while validating one.
        if (!inProgress.Add(nodeId)) return new HashSet<string>(StringComparer.Ordinal);

        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        if (reverseAdjacency.TryGetValue(nodeId, out var parents))
        {
            foreach (var parent in parents)
            {
                if (!ancestors.Add(parent)) continue;
                ancestors.UnionWith(Resolve(parent, reverseAdjacency, memo, inProgress));
            }
        }

        inProgress.Remove(nodeId);

        // Only memoize results computed outside a cycle — a set truncated by the guard above is
        // not the node's real ancestor set and must not be cached for other descendants.
        if (inProgress.Count == 0) memo[nodeId] = ancestors;
        return ancestors;
    }
}
