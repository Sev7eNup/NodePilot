using System.Collections.Concurrent;
using NodePilot.Core.Operations;

namespace NodePilot.Api.Services;

/// <summary>
/// Process-wide cache of each workflow's extracted child-workflow references, keyed by the
/// workflow's <c>UpdatedAt</c>.
/// <para>
/// The Live-Ops snapshot (<c>GET /api/operations/graph</c>) derives the workflow call graph on a
/// 5 s poll, from every open browser, on every page. Deriving it from scratch means loading and
/// JSON-parsing every workflow's <c>DefinitionJson</c>, which is unbounded text including all
/// inline scripts — an expensive way to answer a question whose answer only changes when
/// somebody saves a workflow.
/// </para>
/// <para>
/// Cached are <see cref="WorkflowCallSite"/>s, not resolved edges — a name-based reference
/// resolves against every other workflow's name, so renaming a sibling changes the edge without
/// touching this workflow's definition. Resolution therefore stays per-request in
/// <see cref="WorkflowCallGraphBuilder.BuildFromCallSites"/>, which is dictionary lookups over a
/// handful of refs.
/// </para>
/// <para>
/// Not an RBAC surface: entries are keyed by workflow id and hold only that workflow's own refs.
/// Which of them a caller may see is decided by the identity set handed to the builder, so a cache
/// warmed by an admin cannot widen what a folder-scoped user resolves against.
/// </para>
/// </summary>
public sealed class WorkflowCallSiteCache
{
    /// <summary>
    /// Entry ceiling. Deleted workflows leave their entry behind — nothing tells this cache about
    /// a delete, and asking would cost the very query it exists to avoid — so the map needs
    /// a bound.
    /// <para>
    /// Overflow evicts the oldest entries down to <see cref="EvictTo"/>; it must never drop the
    /// whole map. A global clear would turn one workflow past the ceiling into permanent thrash:
    /// every poll finds everything stale, reloads every definition, and wipes the lot again on the
    /// way out. The headroom between ceiling and target keeps a board just above the ceiling mostly
    /// cached.
    /// </para>
    /// </summary>
    private const int MaxEntries = 8192;

    /// <summary>Entry count an overflow eviction trims the cache down to, so it doesn't recur on
    /// every store.</summary>
    private const int EvictTo = 6144;

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private readonly object _evictionGate = new();
    private long _sequence;

    /// <param name="Sequence">Insertion order, for eviction. Not an LRU stamp: refreshing it on
    /// every read would put a write on the hot path to save re-parsing a definition once.</param>
    private sealed record Entry(DateTime UpdatedAt, IReadOnlyList<WorkflowCallSite> Sites, long Sequence);

    /// <summary>Workflow ids whose cached call sites are missing or older than the given
    /// revision.</summary>
    public List<Guid> StaleIds(IEnumerable<(Guid Id, DateTime UpdatedAt)> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var stale = new List<Guid>();
        foreach (var (id, updatedAt) in current)
        {
            // Inequality, not "older than": a rollback moves UpdatedAt backwards and must still
            // invalidate. The stored stamp is a revision marker, not a clock.
            if (!_entries.TryGetValue(id, out var entry) || entry.UpdatedAt != updatedAt)
                stale.Add(id);
        }
        return stale;
    }

    /// <summary>Stores the call sites extracted from a workflow definition at the given
    /// revision.</summary>
    public void Store(Guid workflowId, DateTime updatedAt, IReadOnlyList<WorkflowCallSite> sites)
    {
        ArgumentNullException.ThrowIfNull(sites);
        if (_entries.Count >= MaxEntries) EvictOldest();
        _entries[workflowId] = new Entry(updatedAt, sites, Interlocked.Increment(ref _sequence));
    }

    /// <summary>
    /// Trims the oldest entries back to <see cref="EvictTo"/>. Gated so concurrent polls do not
    /// each run a full pass; best-effort by design — a missed eviction only means the map is
    /// briefly a few entries over, and every entry it drops costs exactly one definition re-read.
    /// </summary>
    private void EvictOldest()
    {
        lock (_evictionGate)
        {
            var excess = _entries.Count - EvictTo;
            if (excess <= 0) return;
            foreach (var id in _entries.OrderBy(kv => kv.Value.Sequence).Take(excess).Select(kv => kv.Key).ToList())
                _entries.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Cached call sites for the workflow, or an empty list when nothing is cached. Empty is the
    /// honest answer for both "never seen" and "definition holds no calls": neither is an edge.
    /// </summary>
    public IReadOnlyList<WorkflowCallSite> Get(Guid workflowId)
        => _entries.TryGetValue(workflowId, out var entry) ? entry.Sites : [];
}
