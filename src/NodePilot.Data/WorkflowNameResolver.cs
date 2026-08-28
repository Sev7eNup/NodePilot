using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Models;

namespace NodePilot.Data;

/// <summary>
/// Shared by-name workflow resolution for every surface that accepts a workflow name
/// (API by-name endpoints, external trigger, webhooks, engine startWorkflow/forEach).
/// An exact-case match always wins; otherwise the lookup is case-insensitive. Multiple
/// candidates at the winning tier are reported as <see cref="Ambiguity"/> rather than
/// picking an arbitrary row, since Workflow.Name has no unique index.
/// </summary>
public static class WorkflowNameResolver
{
    public enum Outcome { Found, NotFound, Ambiguous }

    public readonly record struct Result(Outcome Outcome, Workflow? Workflow)
    {
        public static Result Found(Workflow w) => new(Outcome.Found, w);
        public static readonly Result NotFound = new(Outcome.NotFound, null);
        public static readonly Result Ambiguous = new(Outcome.Ambiguous, null);
    }

    /// <summary>
    /// Resolves <paramref name="name"/> against <paramref name="source"/> (pass a query with
    /// whatever tracking or includes the caller needs). Runs one case-insensitive query using
    /// ToLower, the only predicate that translates on all three providers (Npgsql, SQL Server,
    /// SQLite), then picks the exact-case tier in memory with ordinal comparison, since doing
    /// it in SQL would inherit the database collation and could defeat the tiebreaker.
    /// </summary>
    public static async Task<Result> ResolveByNameAsync(IQueryable<Workflow> source, string name, CancellationToken ct)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return Result.NotFound;

        // Caps the candidate fetch: the outcome logic only needs to know if a tier has more
        // than one match. This can miss the exact match in the pathological case of more
        // than six case-variant duplicates, which is an accepted tradeoff.
        var lower = trimmed.ToLowerInvariant();
        var candidates = await source.Where(w => w.Name.ToLower() == lower).Take(6).ToListAsync(ct);

        var exact = candidates.Where(w => string.Equals(w.Name, trimmed, StringComparison.Ordinal)).ToList();
        if (exact.Count == 1) return Result.Found(exact[0]);
        if (exact.Count > 1) return Result.Ambiguous;

        return candidates.Count switch
        {
            0 => Result.NotFound,
            1 => Result.Found(candidates[0]),
            _ => Result.Ambiguous,
        };
    }
}
