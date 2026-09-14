using Microsoft.EntityFrameworkCore;
using NodePilot.Data;
using NodePilot.Data.Availability;

namespace NodePilot.Api.Services;

/// <summary>
/// Fills <see cref="WorkflowDefinitionFactsCache"/> shortly after startup.
///
/// <para>
/// The cache lives in memory, so every restart leaves it empty. Whoever opens the workflow list
/// first then pays for reading and parsing every definition — unbounded text including all inline
/// scripts — inside their own request. Doing it here moves that cost off the first user.
/// </para>
///
/// <para>
/// Runs once: afterwards the cache keeps itself current, because entries are keyed by the
/// workflow's <c>UpdatedAt</c> and a save invalidates only its own entry.
/// </para>
/// </summary>
public sealed class WorkflowDefinitionFactsWarmup : BackgroundService
{
    /// <summary>
    /// Definitions read per round-trip. Bounded on purpose: a single query for every definition
    /// would hold all of them in memory at once, which is the cost this class exists to spread.
    /// </summary>
    private const int BatchSize = 50;

    /// <summary>Lets migrations, recovery and the other startup services settle first.</summary>
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WorkflowDefinitionFactsCache _cache;
    private readonly IDatabaseAvailability _availability;
    private readonly ILogger<WorkflowDefinitionFactsWarmup> _logger;

    public WorkflowDefinitionFactsWarmup(
        IServiceScopeFactory scopeFactory,
        WorkflowDefinitionFactsCache cache,
        IDatabaseAvailability availability,
        ILogger<WorkflowDefinitionFactsWarmup> logger)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _availability = availability;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Everything is inside this catch because HostOptions.BackgroundServiceExceptionBehavior is
        // StopHost: anything escaping here would take the whole process down over a warm-up.
        try
        {
            await Task.Delay(StartDelay, stoppingToken).ConfigureAwait(false);
            if (!await _availability.WaitUntilServableAsync(stoppingToken).ConfigureAwait(false))
                return;

            var warmed = await WarmAsync(stoppingToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Workflow definition facts warmed for {Count} workflow(s).", warmed);
        }
        catch (OperationCanceledException)
        {
            // Shutdown during warm-up. Nothing to report.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Workflow definition facts warm-up failed. The first workflow list will populate " +
                "the cache instead.");
        }
    }

    /// <summary>Warms every workflow in batches. Returns how many were resolved.</summary>
    internal async Task<int> WarmAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();

        // Not folder-scoped: entries hold only a workflow's own definition-local facts, and what a
        // caller may see is decided where the facts are used. See WorkflowDefinitionFactsCache.
        var revisions = await db.Workflows.AsNoTracking()
            .Select(w => new { w.Id, w.UpdatedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var warmed = 0;
        foreach (var batch in revisions.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var pairs = batch.Select(w => (w.Id, w.UpdatedAt)).ToList();
            await _cache.ResolveAsync(
                pairs,
                async (ids, token) => await db.Workflows.AsNoTracking()
                    .Where(w => ids.Contains(w.Id))
                    .Select(w => new WorkflowDefinitionRow(w.Id, w.UpdatedAt, w.DefinitionJson))
                    .ToListAsync(token)
                    .ConfigureAwait(false),
                ct).ConfigureAwait(false);
            warmed += pairs.Count;
        }

        return warmed;
    }
}
