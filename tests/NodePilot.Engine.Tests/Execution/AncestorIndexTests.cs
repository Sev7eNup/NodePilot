using System.Collections;
using FluentAssertions;
using NodePilot.Engine.Execution;
using Xunit;

namespace NodePilot.Engine.Tests.Execution;

public sealed class AncestorIndexTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedDiamonds_ResolveWithinAPolynomialTraversalBudget(bool reverseOrder)
    {
        var graph = new Dictionary<string, List<string>>(StringComparer.Ordinal) { ["root"] = [] };
        var expected = new Dictionary<string, string[]> { ["root"] = [] };
        var prefix = new List<string> { "root" };
        var previous = "root";
        for (var layer = 0; layer < 16; layer++)
        {
            var left = $"left-{layer}";
            var right = $"right-{layer}";
            var join = $"join-{layer}";
            graph[left] = [previous];
            graph[right] = [previous];
            graph[join] = [left, right];
            expected[left] = [.. prefix];
            expected[right] = [.. prefix];
            prefix.AddRange([left, right]);
            expected[join] = [.. prefix];
            prefix.Add(join);
            previous = join;
        }

        var ordered = reverseOrder ? graph.Reverse().ToDictionary() : graph;
        var bounded = new BoundedGraph(ordered, graph.Count * graph.Count);

        var index = AncestorIndex.Build(bounded, TestContext.Current.CancellationToken);

        index.Keys.Should().BeEquivalentTo(expected.Keys);
        foreach (var (nodeId, ancestors) in expected)
            index[nodeId].Should().BeEquivalentTo(ancestors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cycles_IncludeEveryReachableAncestorWithoutIncludingTheNode(bool reverseOrder)
    {
        var graph = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["root"] = [],
            ["a"] = ["root", "c"],
            ["b"] = ["a"],
            ["c"] = ["b"],
            ["child"] = ["b"],
            ["self"] = ["self"],
        };
        var ordered = reverseOrder ? graph.Reverse().ToDictionary() : graph;

        var index = AncestorIndex.Build(ordered, TestContext.Current.CancellationToken);

        index["root"].Should().BeEmpty();
        index["a"].Should().BeEquivalentTo("root", "b", "c");
        index["b"].Should().BeEquivalentTo("root", "a", "c");
        index["c"].Should().BeEquivalentTo("root", "a", "b");
        index["child"].Should().BeEquivalentTo("root", "a", "b", "c");
        index["self"].Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancellation_StopsBeforeOrDuringTraversal(bool cancelBeforeTraversal)
    {
        using var cancellation = new CancellationTokenSource();
        var graph = new Dictionary<string, List<string>>
        {
            ["child"] = ["parent"],
            ["parent"] = ["root"],
            ["root"] = [],
        };
        var bounded = new BoundedGraph(graph, graph.Count * graph.Count, cancellation.Cancel);
        if (cancelBeforeTraversal) cancellation.Cancel();

        var build = () => AncestorIndex.Build(bounded, cancellation.Token);

        build.Should().Throw<OperationCanceledException>()
            .Which.CancellationToken.Should().Be(cancellation.Token);
    }

    private sealed class BoundedGraph(
        IReadOnlyDictionary<string, List<string>> graph,
        int lookupBudget,
        Action? onLookup = null) : IReadOnlyDictionary<string, List<string>>
    {
        private int _lookups;

        public bool TryGetValue(string key, out List<string> value)
        {
            if (++_lookups > lookupBudget)
                throw new InvalidOperationException("Ancestor traversal exceeded its polynomial lookup budget.");
            onLookup?.Invoke();
            return graph.TryGetValue(key, out value!);
        }

        public List<string> this[string key] => TryGetValue(key, out var value)
            ? value : throw new KeyNotFoundException(key);
        public IEnumerable<string> Keys => graph.Keys;
        public IEnumerable<List<string>> Values => graph.Values;
        public int Count => graph.Count;
        public bool ContainsKey(string key) => graph.ContainsKey(key);
        public IEnumerator<KeyValuePair<string, List<string>>> GetEnumerator() => graph.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
