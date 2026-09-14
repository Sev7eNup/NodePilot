using NodePilot.Core.Interfaces;
using NodePilot.Data;
using NodePilot.Data.Availability;

namespace NodePilot.Api.Services;

/// <summary>
/// Keeps <see cref="DashboardAggregateCache"/> populated so the dashboard is fast for the person
/// who opens it, not just for the second one.
///
/// <para>
/// A plain TTL cache only ever helps a repeat visit: the first caller of each window still pays the
/// full aggregation, which on a large history is seconds. This service pre-computes the common
/// windows after startup and then refreshes entries shortly before they expire.
/// </para>
///
/// <para>
/// It refreshes only what somebody has actually requested recently (see
/// <see cref="DashboardAggregateCache.RefreshDueAsync"/>), so an instance nobody is looking at does
/// no aggregation at all. Not leader-gated: the cache lives in this process, so every node warms
/// its own.
/// </para>
/// </summary>
public sealed class DashboardAggregateWarmup : BackgroundService
{
    /// <summary>
    /// Windows pre-computed at startup, in the order the UI is likely to need them: the dashboard
    /// always opens on 24 h, so that one is ready first. 1 h is left out — it is cheap enough
    /// without help.
    /// </summary>
    private static readonly int[] PrimedWindows = [24, 168, 720];

    /// <summary>Lets migrations, recovery and the other startup work settle first.</summary>
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(15);

    /// <summary>How often to look for entries that are about to expire.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(20);

    /// <summary>An entry is refreshed once it expires within this span.</summary>
    private static readonly TimeSpan RefreshLead = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How long after its last request an entry is still kept warm. Covers the dashboard's 120 s
    /// poll with room to spare; once a page is closed, its entries stop being refreshed — which is
    /// what keeps an unwatched instance at zero aggregation cost.
    /// </summary>
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(4);

    private readonly DashboardAggregateCache _cache;
    private readonly IDatabaseAvailability _availability;
    private readonly NodePilot.Engine.Security.OutputRedactor _redactor;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DashboardAggregateWarmup> _logger;

    public DashboardAggregateWarmup(
        DashboardAggregateCache cache,
        IDatabaseAvailability availability,
        NodePilot.Engine.Security.OutputRedactor redactor,
        IConfiguration configuration,
        ILogger<DashboardAggregateWarmup> logger)
    {
        _cache = cache;
        _availability = availability;
        _redactor = redactor;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Everything is inside this catch because HostOptions.BackgroundServiceExceptionBehavior is
        // StopHost: anything escaping here would take the whole process down over a cache warm-up.
        try
        {
            if (!_configuration.GetValue("Dashboard:Warmup:Enabled", true))
            {
                _logger.LogDebug("Dashboard aggregate warm-up disabled by configuration.");
                return;
            }

            await Task.Delay(StartDelay, stoppingToken).ConfigureAwait(false);
            if (!await _availability.WaitUntilServableAsync(stoppingToken).ConfigureAwait(false))
                return;

            await PrimeAsync(stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(SweepInterval, stoppingToken).ConfigureAwait(false);
                if (!await _availability.WaitUntilServableAsync(stoppingToken).ConfigureAwait(false))
                    return;
                try
                {
                    await _cache.RefreshDueAsync(RefreshLead, ActiveWindow, stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // One failed sweep must not end the loop — the next one retries.
                    _logger.LogDebug(ex, "Dashboard aggregate refresh failed; retrying next sweep.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown. Nothing to report.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Dashboard aggregate warm-up stopped. The dashboard still works; its first call " +
                "per window pays for the aggregation again.");
        }
    }

    /// <summary>
    /// Pre-computes the common windows for an unrestricted (global Admin) scope. Folder-scoped
    /// callers are not primed — their scope is not known before they ask — but their entries are
    /// kept warm by the refresh sweep once they do.
    /// </summary>
    internal async Task PrimeAsync(CancellationToken ct)
    {
        var accessible = AccessibleFolderSet.Unrestricted;
        var redactor = _redactor;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        foreach (var windowHours in PrimedWindows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _cache.PrimeAsync(
                    DashboardAggregateCache.Key("window", accessible, windowHours),
                    DashboardCacheSettings.Ttl,
                    (db, token) => DashboardHistoricalAggregates.ComputeAsync(
                        db, accessible, windowHours, token),
                    ct).ConfigureAwait(false);

                await _cache.PrimeAsync(
                    DashboardAggregateCache.Key("failure-causes", accessible, windowHours),
                    DashboardCacheSettings.Ttl,
                    (db, token) => new DashboardFailureCauses(db, redactor)
                        .ReadWindowAsync(accessible, windowHours, token),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Warming is best-effort per window: a failure here costs speed, not correctness.
                _logger.LogDebug(ex,
                    "Could not pre-compute dashboard aggregates for {WindowHours} h.", windowHours);
            }
        }

        _logger.LogInformation(
            "Dashboard aggregates pre-computed for {Count} window(s) in {ElapsedMs} ms. Further " +
            "refreshes happen only for windows somebody opens.",
            PrimedWindows.Length, sw.ElapsedMilliseconds);
    }
}

/// <summary>Shared lifetime for the dashboard's cached aggregates.</summary>
internal static class DashboardCacheSettings
{
    /// <summary>
    /// Longer than the dashboard's 120 s poll on purpose: otherwise every poll would miss and
    /// re-run the aggregation the cache exists to avoid. Only historical values live this long —
    /// live counters and the audit feed are never cached.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(150);
}
