using System.Collections;
using NodePilot.Core.Interfaces;

namespace NodePilot.Engine.Execution;

/// <summary>
/// A read-only view over the run's shared step-result map, restricted to the graph ancestors of
/// one step.
///
/// <para>The scheduler keeps every completed step's result in a single dictionary and hands that
/// same dictionary to every step as <c>previousResults</c>. Unrestricted, a step could therefore
/// resolve <c>{{other.output}}</c> from a node on an unrelated parallel branch — but only if that
/// node happened to finish first. The same definition with the same inputs would resolve on one
/// run and fail the unresolved-template check on the next, and a workflow that is reliably green
/// on a fast developer machine could fail intermittently under production load. Scoping the view
/// to ancestors makes the outcome depend on the graph instead of on the scheduler's timing.</para>
///
/// <para>This is a filtering wrapper rather than a copy: a step's ancestor set can be most of the
/// graph, and the map is read on the hot path of every step.</para>
///
/// <para>Note what this does <i>not</i> promise. An ancestor can legitimately have no result —
/// a <c>junction</c> in wait-any mode leaves the losing branch unrun, and a disabled or
/// condition-skipped node never produces one. Those references still fail, and they should:
/// the value genuinely does not exist. What is gone is the case where availability was decided
/// by a race.</para>
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
    /// Asking "did it already run" reintroduces the race this class exists to remove: a sibling
    /// that has finished is recognised as out-of-scope, while the very same reference on the very
    /// same graph reads as an unknown step when the sibling is still running. Callers that treat
    /// out-of-scope as fatal but unknown steps as tolerable — the runScript / custom-activity
    /// exemption in <see cref="StepRunner"/> — then let the literal through on the fast path and
    /// fail on the slow one. Measured end to end: a cross-branch <c>$x = {{sibling.output}}</c>
    /// reached PowerShell verbatim and the run reported green having written
    /// "Ergebnis: {sibling.output}".</para>
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
