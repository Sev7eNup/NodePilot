using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NodePilot.Core.Interfaces;
using NodePilot.Data;

namespace NodePilot.Api.Services;

/// <summary>
/// Short-lived cache for the dashboard's <b>historical</b> aggregates — the parts whose cost grows
/// with the selected window (hourly series, retry ratio, all-time count, failure causes).
///
/// <para>
/// Deliberately not a response cache. The dashboard response also carries live state (running
/// executions, queue counters, heartbeats) and an Admin-only audit feed; caching the whole answer
/// would both stale the live numbers and risk handing one caller's Admin section to another. Only
/// values that are already folder-scoped and role-independent go through here.
/// </para>
///
/// <para>
/// Entries keep the function that produced them, so <see cref="DashboardAggregateWarmup"/> can
/// recompute them before they expire. Without that, the cache only ever helps the *second* visitor:
/// whoever opens a window first still pays the full aggregation.
/// </para>
///
/// <para>
/// Two things this must get right. Concurrent misses share one computation, so a burst of callers
/// after expiry does not multiply the load the cache exists to remove. And each computation runs in
/// its own DI scope with its own DbContext: the request that triggered it may be cancelled and
/// disposed while others still await the result.
/// </para>
/// </summary>
public sealed class DashboardAggregateCache
{
    private readonly IServiceScopeFactory _scopeFactory;
    private sealed class CacheState
    {
        internal readonly ConcurrentDictionary<string, Entry> Entries = new();
        internal readonly ConcurrentDictionary<string, Lazy<Task<object?>>> InFlight = new();
    }
    private CacheState _state = new();

    public DashboardAggregateCache(IServiceScopeFactory scopeFactory)
        => _scopeFactory = scopeFactory;

    /// <summary>
    /// A cached value plus what is needed to produce it again. <see cref="LastRequestedUtc"/> is
    /// what keeps the warm-up honest: it only refreshes entries somebody actually looks at, so an
    /// idle instance does no aggregation at all.
    /// </summary>
    private sealed class Entry
    {
        public object? Value;
        public DateTime ExpiresAtUtc;
        public DateTime LastRequestedUtc;
        public TimeSpan Ttl;
        /// <summary>Set by <see cref="PrimeAsync{T}"/> with keepWarm: refreshed without needing a recent request.</summary>
        public bool Pinned;
        public required Func<NodePilotDbContext, CancellationToken, Task<object?>> Factory;
    }

    /// <summary>
    /// Stable key fragment for a caller's folder permissions. Unrestricted (global Admin) and any
    /// specific folder set are distinct keys, so a scoped caller can never read an unrestricted
    /// aggregate. The id list is hashed rather than concatenated to keep the key bounded.
    /// </summary>
    public static string ScopeKey(AccessibleFolderSet accessible)
    {
        ArgumentNullException.ThrowIfNull(accessible);
        if (accessible.IsUnrestricted) return "all";
        if (accessible.FolderIds.Count == 0) return "none";
        var ordered = string.Join(',', accessible.FolderIds.OrderBy(id => id));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(ordered));
        return "f:" + Convert.ToHexString(digest, 0, 8);
    }

    /// <summary>Builds the full cache key for one aggregate.</summary>
    public static string Key(string aggregate, AccessibleFolderSet accessible, int windowHours)
        => string.Create(CultureInfo.InvariantCulture,
            $"{aggregate}|{ScopeKey(accessible)}|{windowHours}");

    /// <summary>
    /// Returns the cached value for <paramref name="key"/>, computing it if absent or expired.
    /// <paramref name="compute"/> receives a DbContext from a fresh scope and a cancellation token
    /// that is <b>not</b> tied to any one request. It is stored and may be called again later by
    /// the warm-up, so it must not capture a timestamp that defines the window.
    /// </summary>
    public async Task<T> GetOrComputeAsync<T>(
        string key,
        TimeSpan ttl,
        Func<NodePilotDbContext, CancellationToken, Task<T>> compute,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(compute);
        var state = Volatile.Read(ref _state);

        var now = DateTime.UtcNow;
        if (state.Entries.TryGetValue(key, out var cached) && cached.ExpiresAtUtc > now)
        {
            cached.LastRequestedUtc = now;
            return (T)cached.Value!;
        }

        var entry = Remember(state, key, ttl, compute);
        entry.LastRequestedUtc = now;
        return (T)(await RunAsync(state, key, entry, ct).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Computes an entry and stores it without anyone having asked, so the first visitor after a
    /// restart finds it ready — and keeps it eligible for refresh afterwards.
    ///
    /// <para>
    /// An earlier version deliberately left <c>LastRequestedUtc</c> unset here, to stop an
    /// unwatched instance from re-running expensive aggregations forever. That silently disabled
    /// the warm-up: <see cref="RefreshDueAsync"/> only considers entries requested recently, so a
    /// primed entry was never renewed, expired after its TTL and was then dropped. The warm-up
    /// helped only callers arriving within one TTL of startup.
    /// </para>
    /// <para>
    /// The cost concern was real but is addressed at the source instead: the aggregates behind
    /// windows of a day or longer come from precomputed hourly buckets, and shorter windows only
    /// scan their own few rows, so recomputing one is cheap enough to keep warm.
    /// </para>
    /// </summary>
    /// <param name="keepWarm">
    /// Keep refreshing the entry even when nobody requests it (see
    /// <see cref="RefreshDueAsync"/>). Only for aggregates that are cheap to recompute.
    /// </param>
    public async Task PrimeAsync<T>(
        string key,
        TimeSpan ttl,
        Func<NodePilotDbContext, CancellationToken, Task<T>> compute,
        CancellationToken ct,
        bool keepWarm = false)
    {
        ArgumentNullException.ThrowIfNull(compute);
        var state = Volatile.Read(ref _state);
        var entry = Remember(state, key, ttl, compute);
        entry.LastRequestedUtc = DateTime.UtcNow;
        if (keepWarm) entry.Pinned = true;
        await RunAsync(state, key, entry, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Recomputes every entry that is about to expire and was requested recently. Returns how many
    /// were refreshed.
    /// </summary>
    /// <param name="dueWithin">Refresh entries expiring within this span.</param>
    /// <param name="activeWithin">Ignore entries nobody has asked for in this long.</param>
    /// <param name="includePinned">Also refresh pinned entries regardless of the last request.</param>
    public async Task<int> RefreshDueAsync(
        TimeSpan dueWithin, TimeSpan activeWithin, CancellationToken ct, bool includePinned = false)
    {
        var now = DateTime.UtcNow;
        var state = Volatile.Read(ref _state);
        var due = state.Entries
            .Where(kv => kv.Value.ExpiresAtUtc <= now + dueWithin
                         && ((includePinned && kv.Value.Pinned)
                             || kv.Value.LastRequestedUtc >= now - activeWithin))
            .Select(kv => kv.Key)
            .ToList();

        var refreshed = 0;
        foreach (var key in due)
        {
            ct.ThrowIfCancellationRequested();
            if (!state.Entries.TryGetValue(key, out var entry)) continue;
            await RunAsync(state, key, entry, ct).ConfigureAwait(false);
            refreshed++;
        }

        // Drop what is long expired and unused, so a system with many distinct folder scopes does
        // not accumulate entries nobody reads.
        foreach (var (key, entry) in state.Entries)
        {
            if (!entry.Pinned && entry.ExpiresAtUtc < now - activeWithin
                && entry.LastRequestedUtc < now - activeWithin)
                state.Entries.TryRemove(key, out _);
        }

        return refreshed;
    }

    /// <summary>Registers (or updates) what produces this key, keeping any value already held.</summary>
    private Entry Remember<T>(
        CacheState state, string key, TimeSpan ttl, Func<NodePilotDbContext, CancellationToken, Task<T>> compute)
    {
        async Task<object?> Boxed(NodePilotDbContext db, CancellationToken token)
            => await compute(db, token).ConfigureAwait(false);

        return state.Entries.AddOrUpdate(
            key,
            _ => new Entry { Factory = Boxed, Ttl = ttl, ExpiresAtUtc = DateTime.MinValue },
            (_, existing) =>
            {
                existing.Factory = Boxed;
                existing.Ttl = ttl;
                return existing;
            });
    }

    /// <summary>
    /// Runs an entry's factory, sharing one computation across concurrent callers and storing the
    /// result. A failure is never stored as an answer.
    /// </summary>
    private async Task<object?> RunAsync(CacheState state, string key, Entry entry, CancellationToken ct)
    {
        // ExecutionAndPublication: the factory runs once even when several callers miss together;
        // everyone else awaits the same task.
        var lazy = state.InFlight.GetOrAdd(key, _ => new Lazy<Task<object?>>(
            () => ComputeAsync(state, key, entry),
            LazyThreadSafetyMode.ExecutionAndPublication));

        // WaitAsync, not a cancellable compute: a caller walking away must not cancel the shared
        // work the remaining callers are waiting for.
        return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<object?> ComputeAsync(CacheState state, string key, Entry entry)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
            var value = await entry.Factory(db, CancellationToken.None).ConfigureAwait(false);
            entry.Value = value;
            entry.ExpiresAtUtc = DateTime.UtcNow + entry.Ttl;
            return value;
        }
        finally
        {
            // Always drop the in-flight marker, including on failure: a failed computation must be
            // retried by the next caller, never cached as an answer.
            state.InFlight.TryRemove(key, out _);
        }
    }

    /// <summary>Invalidates after a committed folder mutation; old warm-up work stays in its old generation.</summary>
    internal void Clear()
    {
        Interlocked.Exchange(ref _state, new CacheState());
    }

    /// <summary>Test seam: how many entries are currently tracked.</summary>
    internal int Count => Volatile.Read(ref _state).Entries.Count;
}
