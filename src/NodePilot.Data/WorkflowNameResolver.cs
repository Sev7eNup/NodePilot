using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Models;

namespace NodePilot.Data;

/// <summary>
/// Shared by-name workflow resolution for every surface that accepts a workflow NAME
/// (API by-name endpoints, external trigger, webhooks, engine startWorkflow/forEach).
/// Semantics: an exact-case match always wins; otherwise the lookup is case-insensitive.
/// More than one candidate at the winning tier is reported as <see cref="Ambiguity"/>
/// instead of silently picking an arbitrary row — Workflow.Name carries no unique index,
/// so "Daily" and "daily" (or two identical names) can legitimately coexist.
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
    /// Resolve <paramref name="name"/> against <paramref name="source"/> (pass the query
    /// with whatever tracking/includes the caller needs). One case-insensitive query
    /// (ToLower is the only CI predicate that translates on all three providers —
    /// Npgsql/SQL Server/SQLite; EF.Functions.ILike is Postgres-only), then the
    /// exact-case tier is decided IN MEMORY with ordinal comparison. Deciding the exact
    /// tier in SQL would inherit the database collation — on SQL Server's typical CI
    /// default, <c>Name == @p</c> matches case-insensitively and the exact-case
    /// tiebreaker would silently stop working.
    /// </summary>
    public static async Task<Result> ResolveByNameAsync(IQueryable<Workflow> source, string name, CancellationToken ct)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return Result.NotFound;

        // Cap the candidate fetch: >1 per tier is all the outcome logic needs. The cap
        // only matters for the pathological ">6 case-variant duplicates and exactly one
        // exact match outside the sample" corner, which we accept.
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
