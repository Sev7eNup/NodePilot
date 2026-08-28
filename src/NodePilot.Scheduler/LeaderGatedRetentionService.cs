using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodePilot.Core.Interfaces;
using NodePilot.Data;
using NodePilot.Data.Availability;
using NodePilot.Scheduler.Options;

namespace NodePilot.Scheduler;

/// <summary>
/// Shared frame of the leader-gated retention sweeps (executions, audit log, workflow versions,
/// support events, notification ledger). It owns exactly the part that was identical in all five
/// services: the cold-start warm-up, the started/stopped log pair, and the pass loop with its
/// availability gate -> leader gate -> one iteration -> interval delay.
///
/// <para>
/// The sweep itself stays in the derived <see cref="RunIterationAsync"/>, deliberately: its broad
/// <c>catch (Exception)</c> must remain one level BELOW the host-fatal boundary, because
/// <c>HostOptions.BackgroundServiceExceptionBehavior</c> is left at <c>StopHost</c> and anything
/// escaping this loop takes the host with it. The only catch this class owns is the
/// <see cref="OperationCanceledException"/> that turns a shutdown into a clean loop exit.
/// </para>
/// </summary>
public abstract class LeaderGatedRetentionService : BackgroundService
{
    protected readonly IServiceScopeFactory _scopeFactory;
    // Hot-reload: hold the live monitor (not a cached snapshot) so a config edit of the sweep's
    // Retention:* section takes effect on the next pass without a restart.
    protected readonly IOptionsMonitor<RetentionOptions> _opts;
    // Non-generic on purpose: each derived service passes its own ILogger<TService>, so the log
    // category stays the concrete service.
    protected readonly ILogger _logger;

    private readonly IClusterStateProvider _cluster;
    private readonly IDatabaseAvailability _availability;

    protected LeaderGatedRetentionService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<RetentionOptions> opts,
        IClusterStateProvider cluster,
        ILogger logger,
        IDatabaseAvailability availability)
    {
        _scopeFactory = scopeFactory;
        _opts = opts;
        _cluster = cluster;
        _availability = availability;
        _logger = logger;
    }

    /// <summary>
    /// The concrete service name — used verbatim in the two lifecycle log lines and as the
    /// heartbeat key in <c>SystemHealth</c>.
    /// </summary>
    protected abstract string ServiceName { get; }

    /// <summary>Cold-start grace before the first pass.</summary>
    protected abstract TimeSpan WarmUpDelay { get; }

    /// <summary>Lower bound (minutes) for the inter-pass delay.</summary>
    protected abstract int MinIntervalMinutes { get; }

    /// <summary>Live <c>IntervalMinutes</c> of this sweep's options section — read per
    /// pass.</summary>
    protected abstract int ConfiguredIntervalMinutes { get; }

    /// <summary>Value of the <c>nodepilot.retention.service</c> metric tag.</summary>
    protected abstract string MetricServiceTag { get; }

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(WarmUpDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        // Composed instead of a literal so every service keeps the exact message (and template)
        // it logged before the five loops were merged into this base.
#pragma warning disable CA2254
        _logger.LogInformation(ServiceName + " started (hot-reload: per-pass config).");
#pragma warning restore CA2254

        while (!stoppingToken.IsCancellationRequested)
        {
            // Availability gate, deliberately ABOVE the leader check: during a database outage no
            // node can renew its cluster lease, so every node reads as a follower - gating on
            // IsLeader first would park for the right reason and log the wrong one.
            // Returns false only on shutdown and never throws (BackgroundServiceExceptionBehavior
            // is left at its default StopHost, so an escaping cancellation would stop the host).
            if (!await _availability.WaitUntilServableAsync(stoppingToken)) break;

            // HA gate: only the leader sweeps. Otherwise a follower would contend on the same
            // DELETEs and double the IO cost on the shared DB.
            if (!_cluster.IsLeader)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                await RunIterationAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }

            var interval = TimeSpan.FromMinutes(Math.Max(MinIntervalMinutes, ConfiguredIntervalMinutes));
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

#pragma warning disable CA2254
        _logger.LogInformation(ServiceName + " stopped.");
#pragma warning restore CA2254
    }

    /// <summary>
    /// Exactly one sweep iteration. Implemented by the derived service — including its own broad
    /// catch, which must stay below the host-fatal boundary (see the class remarks). No
    /// <c>Task.Delay</c> in there: this class owns the inter-pass spacing.
    /// </summary>
    internal abstract Task RunIterationAsync(CancellationToken ct);

    /// <summary>Metric tags for this sweep: the single <c>nodepilot.retention.service</c>
    /// tag.</summary>
    protected TagList RetentionTags() => new TagList { new("nodepilot.retention.service", MetricServiceTag) };

    /// <summary>Liveness beat recorded under <see cref="ServiceName"/>.</summary>
    protected async Task HeartbeatAsync(int intervalMinutes, string status, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        await SystemHealthWriter.BeatAsync(db, ServiceName,
            expectedIntervalSeconds: intervalMinutes * 60, status: status, ct: ct);
    }
}
