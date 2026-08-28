using System.Collections;
using NodePilot.Core.Interfaces;

namespace NodePilot.Engine.Execution;

/// <summary>
/// A read-only view over the run's shared step-result map, restricted to the graph ancestors of
/// one step.
///
/// <para>The scheduler keeps every completed step's result in one dictionary shared by all
/// steps. Without scoping, a step could resolve <c>{{other.output}}</c> from an unrelated
/// parallel branch whenever that branch happened to finish first — the same workflow would then
/// resolve or fail an unresolved-template check depending on timing. Scoping to ancestors makes
/// the outcome depend on the graph instead of on scheduler timing.</para>
///
/// <para>This is a filtering wrapper rather than a copy, since a step's ancestor set can be most
/// of the graph and the map is read on the hot path of every step.</para>
///
/// <para>An ancestor can still legitimately have no result — a <c>junction</c> in wait-any mode
/// leaves the losing branch unrun, and a disabled or condition-skipped node never produces one.
/// Those references still fail correctly; only the race-based case is removed.</para>
/// </summary>
internal sealed class AncestorScopedResults : IReadOnlyDictionary<string, ActivityResult>
{
    private readonly IReadOnlyDictionary<string, ActivityResult> _all;
    private readonly IReadOnlySet<string> _ancestors;
    private readonly IReadOnlySet<string> _knownNodeIds;

    public AncestorScopedResults(
        IReadOnlyDictionary<string, ActivityResult> all,
        IReadOnlySet<string> ancestors,
        IReadOnlySet<string> knownNodeIds)
    {
        _all = all;
        _ancestors = ancestors;
        _knownNodeIds = knownNodeIds;
    }

    public bool TryGetValue(string key, out ActivityResult value)
    {
        if (!_ancestors.Contains(key))
        {
            value = null!;
            return false;
        }

        return _all.TryGetValue(key, out value!);
    }

    public bool ContainsKey(string key) => _ancestors.Contains(key) && _all.ContainsKey(key);

    /// <summary>
    /// True when <paramref name="stepId"/> names a node of this workflow that is not a predecessor
    /// of the step holding this view — the databus hides it by design, whether or not it has
    /// produced a result yet.
    ///
    /// <para>The membership test is deliberately the compiled node set and not the result map.
    /// Asking "did it already run" instead would reintroduce the race this class exists to
    /// remove: a finished sibling would read as out-of-scope, while the same reference on the
    /// same graph reads as an unknown step while the sibling is still running. Callers that treat
    /// out-of-scope as fatal but unknown steps as tolerable — the runScript / custom-activity
    /// exemption in <see cref="StepRunner"/> — would then let a cross-branch reference through as
    /// a literal string on the fast path instead of failing.</para>
    ///
    /// <para>Reference by an <c>outputVariable</c> alias is resolved to the node id by the caller,
    /// which owns that mapping.</para>
    /// </summary>
    public bool IsNonAncestorNode(string stepId) =>
        _knownNodeIds.Contains(stepId) && !_ancestors.Contains(stepId);

    public ActivityResult this[string key] =>
        TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);

    public IEnumerable<string> Keys => Visible.Select(kv => kv.Key);

    public IEnumerable<ActivityResult> Values => Visible.Select(kv => kv.Value);

    public int Count => Visible.Count();

    public IEnumerator<KeyValuePair<string, ActivityResult>> GetEnumerator() => Visible.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Enumerates the ancestor set rather than the full map: the ancestor set is normally the
    /// smaller of the two, and iterating it keeps enumeration independent of how many unrelated
    /// branches happen to have finished.
    /// </summary>
    private IEnumerable<KeyValuePair<string, ActivityResult>> Visible
    {
        get
        {
            foreach (var id in _ancestors)
            {
                if (_all.TryGetValue(id, out var result))
                    yield return new KeyValuePair<string, ActivityResult>(id, result);
            }
        }
    }
}
