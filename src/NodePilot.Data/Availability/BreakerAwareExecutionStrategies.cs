using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
// The two providers keep their retrying strategy in different namespaces: SQL Server in the root
// Microsoft.EntityFrameworkCore namespace, Npgsql in its own. Both are the documented base for
// customising retry behaviour.
using Npgsql.EntityFrameworkCore.PostgreSQL;

namespace NodePilot.Data.Availability;

/// <summary>
/// Shared decision for both providers' breaker-aware retry strategies, so the rule exists once.
/// </summary>
internal static class BreakerAwareRetryDecision
{
    /// <summary>
    /// Returns <c>false</c> to veto a retry outright, <c>true</c> to let the provider's own strategy
    /// decide as before.
    ///
    /// <para><b>Never retry a command timeout.</b> This is the single highest-value line in the whole
    /// change. With the shipped defaults — <c>CommandTimeout(120)</c> and
    /// <c>EnableRetryOnFailure(5, 10s)</c> — one slow query costs
    /// (1 + 5) x 120 s plus backoff, about <b>751 seconds</b>. Retrying a statement that already
    /// exceeded its budget cannot help: if the database were fast enough it would have answered the
    /// first time.</para>
    ///
    /// <para><b>Never retry while the breaker is open.</b> The database is known to be gone; six more
    /// attempts only hold a connection and a thread-pool continuation for minutes.</para>
    ///
    /// <para>Everything else still delegates to the provider's own transient-error list, so deadlocks,
    /// PostgreSQL 53300 (<c>too_many_connections</c>) and Azure throttling keep retrying exactly as
    /// before — including the PK-violation idempotency pairing in
    /// <c>WorkflowDbWriteMetrics.SaveChangesIdempotentAsync</c> that makes those retries safe.</para>
    ///
    /// <para>While <see cref="DatabaseAvailabilityState.Booting"/> and
    /// <see cref="DatabaseAvailabilityState.Armed"/> the answer is "delegate": the boot block must keep
    /// its full resilience against a database that is still coming up, and Armed is by definition the
    /// state in which nothing has been decided yet.</para>
    /// </summary>
    public static bool ShouldConsiderRetry(IDatabaseAvailability availability, Exception? exception)
    {
        if (DbErrorClassifier.Classify(exception) is DbFailureKind.CommandTimeout) return false;
        return availability.State is not DatabaseAvailabilityState.Unavailable;
    }
}

/// <summary>
/// SQL Server retry strategy that consults the availability breaker.
/// <para>Derives from <see cref="SqlServerRetryingExecutionStrategy"/> rather than replacing it so the
/// provider's transient-error list is preserved verbatim.</para>
/// </summary>
public sealed class BreakerAwareSqlServerExecutionStrategy(
    ExecutionStrategyDependencies dependencies,
    int maxRetryCount,
    TimeSpan maxRetryDelay,
    IEnumerable<int>? errorNumbersToAdd,
    IDatabaseAvailability availability)
    : SqlServerRetryingExecutionStrategy(dependencies, maxRetryCount, maxRetryDelay, errorNumbersToAdd)
{
    protected override bool ShouldRetryOn(Exception exception)
        => BreakerAwareRetryDecision.ShouldConsiderRetry(availability, exception)
           && base.ShouldRetryOn(exception);
}

/// <summary>
/// PostgreSQL counterpart of <see cref="BreakerAwareSqlServerExecutionStrategy"/>. Separate class
/// because the two providers' base strategies take different final constructor arguments and C# has no
/// multiple inheritance; the decision itself lives once in
/// <see cref="BreakerAwareRetryDecision"/>.
/// </summary>
public sealed class BreakerAwareNpgsqlExecutionStrategy(
    ExecutionStrategyDependencies dependencies,
    int maxRetryCount,
    TimeSpan maxRetryDelay,
    ICollection<string>? errorCodesToAdd,
    IDatabaseAvailability availability)
    : NpgsqlRetryingExecutionStrategy(dependencies, maxRetryCount, maxRetryDelay, errorCodesToAdd)
{
    // Npgsql declares this parameter nullable where SQL Server does not; match each base exactly.
    protected override bool ShouldRetryOn(Exception? exception)
        => BreakerAwareRetryDecision.ShouldConsiderRetry(availability, exception)
           && base.ShouldRetryOn(exception);
}
