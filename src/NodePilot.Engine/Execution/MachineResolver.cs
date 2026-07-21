using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Engine.Execution;

internal static class MachineResolver
{
    // Process-wide cache of "this string resolves to no registered machine" — i.e. callers
    // hit the ad-hoc fallback. Stress-test workloads with 500 workflows × dozens of steps
    // all targeting the same handful of strings (e.g. "localhost") would otherwise pound
    // the DB with thousands of identical lookups that always return nothing. TTL keeps the
    // cache from going stale when an operator registers a new machine while traffic is
    // hot. Hits for a *registered* machine are not cached, since EF tracking semantics
    // require fresh per-context entities.
    private static readonly ConcurrentDictionary<string, DateTime> _adHocCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan AdHocCacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Resolves a raw target machine value to a <see cref="ManagedMachine"/> instance.
    /// Supports: GUID lookup, hostname/name lookup, or ad-hoc machine from hostname.
    /// Returns null only if the input is empty.
    /// Uses the supplied DbContext — callers from parallel step-paths pass a scope-local
    /// context to avoid racing on the engine's shared DbContext.
    /// </summary>
    internal static async Task<ManagedMachine?> ResolveAsync(
        NodePilotDbContext db, string? resolved, ILogger logger, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(resolved))
            return null;

        // Skip the DB roundtrip entirely when we recently confirmed this string maps to
        // no registered machine. The resulting ad-hoc machine is purely synthesized from
        // the hostname, so there's nothing to refresh from the database.
        if (_adHocCache.TryGetValue(resolved, out var expires) && expires > DateTime.UtcNow)
            return AdHoc(resolved);

        // Direct GUID lookup
        if (Guid.TryParse(resolved, out var guid))
        {
            var found = await db.ManagedMachines.FindAsync(new object[] { guid }, ct);
            if (found is not null) return found;
        }

        // Lookup by hostname or name
        var machine = await db.ManagedMachines
            .FirstOrDefaultAsync(m => m.Hostname == resolved || m.Name == resolved, ct);

        if (machine is not null)
            return machine;

        // Not registered — build ad-hoc machine from hostname; remember the negative result
        // so the next 30 seconds of identical lookups skip the two SELECTs above.
        _adHocCache[resolved] = DateTime.UtcNow.Add(AdHocCacheTtl);
        logger.LogInformation("Machine '{Value}' not registered, using ad-hoc hostname", resolved);
        return AdHoc(resolved);
    }

    private static ManagedMachine AdHoc(string resolved) => new()
    {
        Id = Guid.Empty, // marker: ad-hoc
        Name = resolved,
        Hostname = resolved,
        WinRmPort = 5985,
        UseSsl = false,
    };

    /// <summary>
    /// Test/admin hook: drop the negative cache, e.g. after registering a new machine via
    /// the API so the next workflow step picks it up without waiting for the TTL.
    /// </summary>
    internal static void InvalidateCache() => _adHocCache.Clear();
}
