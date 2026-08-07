using Microsoft.EntityFrameworkCore;

namespace NodePilot.Data.Availability;

/// <summary>
/// Applies a shorter command timeout to a scoped block and restores the previous one afterwards.
///
/// <para><b>Why this needs a type instead of two lines at the call site.</b> The DbContext is pooled
/// and request-scoped: the same instance the middleware stamps is the one the controller then uses. A
/// budget that is not restored — on every path, including the ones that throw — silently applies to
/// everything downstream, and because the pool recycles instances it can outlive the request entirely
/// and poison an unrelated one later. The existing hand-rolled <c>try/finally</c> in
/// <c>WorkflowsController</c> is replaced by this so the pattern exists once.</para>
///
/// <para>Restoring writes back the previous value including <c>null</c>. That distinction matters:
/// <c>null</c> means "use the provider default", and replacing it with a fabricated number would
/// silently change behaviour everywhere the context is reused.</para>
/// </summary>
public readonly struct DatabaseCommandBudget : IDisposable
{
    private readonly DbContext? _context;
    private readonly int? _previous;

    private DatabaseCommandBudget(DbContext context, int? previous)
    {
        _context = context;
        _previous = previous;
    }

    /// <summary>
    /// Stamps <paramref name="seconds"/> onto <paramref name="context"/> until the returned value is
    /// disposed. A non-positive budget is a no-op, so a misconfigured key cannot accidentally set a
    /// zero (= infinite, on some providers) timeout.
    /// </summary>
    public static DatabaseCommandBudget Apply(DbContext context, int seconds)
    {
        if (seconds <= 0) return default;

        var previous = context.Database.GetCommandTimeout();
        context.Database.SetCommandTimeout(seconds);
        return new DatabaseCommandBudget(context, previous);
    }

    public void Dispose() => _context?.Database.SetCommandTimeout(_previous);
}
