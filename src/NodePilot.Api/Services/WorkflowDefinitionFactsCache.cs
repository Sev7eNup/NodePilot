using System.Collections.Concurrent;
using NodePilot.Core.WorkflowDefinitions;

namespace NodePilot.Api.Services;

/// <summary>
/// Process-wide cache of the facts derived from each workflow's definition, keyed by the
/// workflow's <c>UpdatedAt</c>.
/// <para>
/// Several endpoints answer questions that live inside <c>DefinitionJson</c> — the live-ops
/// snapshot needs the child-workflow call graph, the machines list needs which machines a
/// workflow targets, the workflow list needs whether starting one asks for input. Deriving any
/// of them from scratch means loading and JSON-parsing every workflow's definition, which is
/// unbounded text including all inline scripts. Every one of those answers changes only when
/// somebody saves a workflow, never when somebody polls.
/// </para>
/// <para>
/// Cached are the definition-local facts, not anything that depends on other workflows. A
/// name-based child reference resolves against every other workflow's name, so renaming a
/// sibling changes the edge without touching this workflow's definition; resolution therefore
/// stays per-request in <see cref="NodePilot.Core.Operations.WorkflowCallGraphBuilder.BuildFromCallSites"/>,
/// which is dictionary lookups over a handful of refs.
/// </para>
/// <para>
/// Not an RBAC surface: entries are keyed by workflow id and hold only that workflow's own
/// facts. What a caller may see is decided where the facts are used, so a cache warmed by an
/// admin cannot widen what a folder-scoped user resolves against.
/// </para>
/// </summary>
public sealed class WorkflowDefinitionFactsCache
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
    private sealed record Entry(DateTime UpdatedAt, WorkflowDefinitionFacts Facts, long Sequence);

    /// <summary>Workflow ids whose cached facts are missing or older than the given
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

    /// <summary>Stores the facts extracted from a workflow definition at the given
    /// revision.</summary>
    public void Store(Guid workflowId, DateTime updatedAt, WorkflowDefinitionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (_entries.Count >= MaxEntries) EvictOldest();
        _entries[workflowId] = new Entry(updatedAt, facts, Interlocked.Increment(ref _sequence));
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
    /// Cached facts for the workflow, or empty facts when nothing is cached. Empty is the honest
    /// answer for both "never seen" and "definition holds none of this": neither is an edge, a
    /// machine reference, or a parameter prompt.
    /// </summary>
    public WorkflowDefinitionFacts Get(Guid workflowId)
        => _entries.TryGetValue(workflowId, out var entry) ? entry.Facts : WorkflowDefinitionFacts.Empty;

    /// <summary>
    /// Facts for each of the given workflows, reading and extracting only the definitions whose
    /// revision moved. In the steady state that is none.
    /// <para>
    /// The returned map is request-local and authoritative for this response. It is deliberately
    /// not a second read of the shared cache after storing: two requests racing across a save
    /// could interleave into a mixed answer, and an eviction landing mid-request would silently
    /// drop facts this request had already extracted.
    /// </para>
    /// </summary>
    /// <param name="loadDefinitions">
    /// Reads <c>(Id, UpdatedAt, DefinitionJson)</c> for the given ids. Supplied by the caller so
    /// the query stays inside the caller's RBAC scope.
    /// </param>
    public async Task<Dictionary<Guid, WorkflowDefinitionFacts>> ResolveAsync(
        IReadOnlyCollection<(Guid Id, DateTime UpdatedAt)> workflows,
        Func<IReadOnlyList<Guid>, CancellationToken, Task<IReadOnlyList<WorkflowDefinitionRow>>> loadDefinitions,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        ArgumentNullException.ThrowIfNull(loadDefinitions);

        var resolved = new Dictionary<Guid, WorkflowDefinitionFacts>(workflows.Count);

        var staleIds = StaleIds(workflows);
        if (staleIds.Count > 0)
        {
            foreach (var row in await loadDefinitions(staleIds, ct))
            {
                var facts = WorkflowDefinitionFacts.Extract(row.DefinitionJson);
                Store(row.Id, row.UpdatedAt, facts);
                resolved[row.Id] = facts;
            }
        }

        foreach (var (id, _) in workflows)
        {
            if (!resolved.ContainsKey(id)) resolved[id] = Get(id);
        }

        return resolved;
    }
}

/// <summary>The columns <see cref="WorkflowDefinitionFactsCache.ResolveAsync"/> needs to refresh
/// one entry.</summary>
public sealed record WorkflowDefinitionRow(Guid Id, DateTime UpdatedAt, string DefinitionJson);
